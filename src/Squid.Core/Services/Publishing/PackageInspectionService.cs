using Serilog;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

public sealed class PackageInspectionService(IEnumerable<IPackageInspector> inspectors) : IPackageInspectionService
{
    private const string ListingStep = "Listing";

    public async Task<PackageFacts> InspectAsync(string path, BuiltInPackageFormat format, CancellationToken ct)
    {
        var reader = new PackageArchiveTextReader(path, format);

        try
        {
            var context = new PackageInspectionContext(reader, await reader.ListFilesAsync(ct).ConfigureAwait(false));

            foreach (var inspector in inspectors) await RunAsync(inspector, context, ct).ConfigureAwait(false);

            return context.Facts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailureOnly(ListingStep, ex);
        }
    }

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

    private static PackageFacts FailureOnly(string step, Exception ex)
    {
        Log.Warning(ex, "Package inspection step {Step} failed; the push goes on without facts", step);

        return new PackageFacts { Values = { [PackageFactKeys.InspectorFailedPrefix + step] = ex.Message } };
    }
}
