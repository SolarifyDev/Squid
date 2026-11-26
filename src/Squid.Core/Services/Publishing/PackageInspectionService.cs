using Serilog;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

internal sealed class PackageInspectionService(IEnumerable<IPackageInspector> inspectors) : IPackageInspectionService
{
    /// <summary>The most the stored facts may take as JSON. Values are capped one by one; this is the backstop for an inspector that records a great many.</summary>
    public const int MaxFactsJsonLength = 32 * 1024;

    private const string ContextStep = "Context";

    public PackageArchiveScanner CreateScanner() => new(Paths(i => i.FilesToProbe.Concat(i.FilesToRead)), Paths(i => i.FilesToRead));

    public async Task<PackageFacts> InspectAsync(string path, BuiltInPackageFormat format, PackageArchiveScan scan, CancellationToken ct)
    {
        try
        {
            var context = new PackageInspectionContext(scan, (file, token) => new PackageArchiveTextReader(path, format).TryReadTextFileAsync(file, token));

            foreach (var inspector in inspectors) await RunAsync(inspector, context, ct).ConfigureAwait(false);

            return Bounded(context.Facts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailureOnly(ContextStep, ex);
        }
    }

    private HashSet<string> Paths(Func<IPackageInspector, IEnumerable<string>> select) => inspectors.SelectMany(select).ToHashSet(StringComparer.Ordinal);

    private static async Task RunAsync(IPackageInspector inspector, PackageInspectionContext context, CancellationToken ct)
    {
        try
        {
            await inspector.InspectAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var name = inspector.GetType().Name;

            Log.Warning(ex, "Package inspector {Inspector} failed; the push goes on without its facts", name);

            context.Set(PackageFactKeys.InspectorFailedPrefix + name, ex.Message);
        }
    }

    // Keeps facts in key order while they fit, then says it dropped the rest.
    private static PackageFacts Bounded(PackageFacts facts)
    {
        if (PackageFactsJson.Serialize(facts).Length <= MaxFactsJsonLength) return facts;

        var kept = new PackageFacts { Values = { [PackageFactKeys.FactsTruncated] = PackageFactKeys.True } };

        foreach (var pair in facts.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            kept.Values[pair.Key] = pair.Value;

            if (PackageFactsJson.Serialize(kept).Length > MaxFactsJsonLength) kept.Values.Remove(pair.Key);
        }

        Log.Warning("Package facts were {Length} characters of JSON; kept {Kept} of {Total} facts", PackageFactsJson.Serialize(facts).Length, kept.Values.Count, facts.Values.Count);

        return kept;
    }

    private static PackageFacts FailureOnly(string step, Exception ex)
    {
        Log.Warning(ex, "Package inspection step {Step} failed; the push goes on without facts", step);

        return new PackageFacts { Values = { [PackageFactKeys.InspectorFailedPrefix + step] = PackageInspectionContext.Cap(ex.Message) } };
    }
}
