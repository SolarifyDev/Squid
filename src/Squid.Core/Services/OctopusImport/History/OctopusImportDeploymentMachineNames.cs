using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.Core.Services.OctopusImport.History;

/// <summary>
/// Display names of the machines a deployment ran on, aligned with <c>DeployedToMachineIds</c>. Machines are never
/// imported, so a name comes from a Machine document in the export, else from the deployment itself (the server
/// fetch records the names positionally beside the ids), else it stays unknown (null).
/// </summary>
public static class OctopusImportDeploymentMachineNames
{
    public static IReadOnlyList<string> Resolve(OctopusDeploymentDto deployment, IReadOnlyDictionary<string, string> exportedNames)
    {
        var ids = deployment.DeployedToMachineIds ?? [];
        var recorded = RecordedNames(deployment, ids.Count);

        return ids.Select((id, index) => exportedNames?.GetValueOrDefault(id ?? string.Empty) ?? recorded[index]).ToList();
    }

    // Positional: a recorded list whose length differs from the ids cannot be aligned, so it is ignored as a whole.
    private static string[] RecordedNames(OctopusDeploymentDto deployment, int count)
    {
        var names = ReadExtension(deployment);

        return names.Count == count ? names.Select(n => string.IsNullOrWhiteSpace(n) ? null : n.Trim()).ToArray() : new string[count];
    }

    private static List<string> ReadExtension(OctopusDeploymentDto deployment)
    {
        if (deployment.ExtensionData == null || !deployment.ExtensionData.TryGetValue(OctopusRestHistoryFetcher.DeployedToMachineNamesProperty, out var element)) return [];

        if (element.ValueKind != JsonValueKind.Array) return [];

        return element.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList();
    }
}
