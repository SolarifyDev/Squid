using System.Text.Json;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing.Inspectors;

/// <summary>What <c>package.json</c> says: whether it builds or starts, and the Node version it asks for. A file that is not valid JSON is a fact, not a failure.</summary>
public sealed class NodeInspector : IPackageInspector
{
    public IReadOnlyCollection<string> FilesToRead { get; } = ["package.json"];

    public async Task InspectAsync(PackageInspectionContext context, CancellationToken ct)
    {
        if (!context.HasFile("package.json")) return;

        context.SetTrue(PackageFactKeys.NodePackageJson);

        var text = await context.TryReadTextAsync("package.json", ct).ConfigureAwait(false);

        if (text != null) Record(context, text);
    }

    private static void Record(PackageInspectionContext context, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            RecordScripts(context, document.RootElement);
            RecordEngines(context, document.RootElement);
        }
        // An invalid UTF-16 surrogate in a string makes reading it throw InvalidOperationException rather than JsonException.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            context.Set(PackageFactKeys.NodeError, ex.Message);
        }
    }

    private static void RecordScripts(PackageInspectionContext context, JsonElement root)
    {
        if (!TryObject(root, "scripts", out var scripts)) return;

        if (scripts.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.String) context.SetTrue(PackageFactKeys.NodeBuildScript);

        if (scripts.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.String) context.SetTrue(PackageFactKeys.NodeStartScript);
    }

    private static void RecordEngines(PackageInspectionContext context, JsonElement root)
    {
        if (TryObject(root, "engines", out var engines) && engines.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.String)
            context.Set(PackageFactKeys.NodeEngines, node.GetString());
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement child)
    {
        child = default;

        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object;
    }
}
