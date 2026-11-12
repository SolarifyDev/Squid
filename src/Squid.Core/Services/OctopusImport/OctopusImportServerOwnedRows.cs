using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// History rows (Release, Deployment, ServerTask) are decided by the server from the source map, never by
/// the client: a posted plan's history rows are replaced by the rows of the server's own preview. A client
/// may also omit them to keep the request small.
/// </summary>
public static class OctopusImportServerOwnedRows
{
    private static readonly HashSet<string> HistorySourceTypes = new(StringComparer.OrdinalIgnoreCase) { nameof(OctopusResourceKind.Release), nameof(OctopusResourceKind.Deployment), nameof(OctopusResourceKind.ServerTask) };

    public static OctopusImportPreviewPlanDto MergeServerOwnedHistoryRows(OctopusImportPreviewPlanDto clientPlan, OctopusImportPreviewPlanDto serverPlan)
    {
        ArgumentNullException.ThrowIfNull(serverPlan);

        if (clientPlan == null) return serverPlan;

        clientPlan.Resources = clientPlan.Resources.Where(r => !IsHistoryRow(r)).Concat(serverPlan.Resources.Where(IsHistoryRow)).ToList();

        return clientPlan;
    }

    private static bool IsHistoryRow(OctopusImportResourceResultDto resource) => HistorySourceTypes.Contains(resource.SourceType ?? string.Empty);
}
