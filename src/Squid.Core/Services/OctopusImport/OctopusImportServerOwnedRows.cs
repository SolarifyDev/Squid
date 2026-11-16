using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Rows the server decides, never the client: history rows (Release, Deployment, ServerTask), decided from the source map, and every row
/// the user decides about (and the rows that follow it), decided by the server's plan built with the user's selection. A posted plan's
/// server-owned rows are replaced by the rows of the server's own preview, and its summary by the server's (the counts follow the
/// selection). Rows outside the session's project selection are server-owned as well. A client may also omit server-owned rows to keep the request small. A posted row claiming a choice the server does not
/// offer is kept as posted, for validation to refuse.
/// </summary>
public static class OctopusImportServerOwnedRows
{
    private static readonly HashSet<string> HistorySourceTypes = new(StringComparer.OrdinalIgnoreCase) { nameof(OctopusResourceKind.Release), nameof(OctopusResourceKind.Deployment), nameof(OctopusResourceKind.ServerTask) };

    public static OctopusImportPreviewPlanDto MergeServerOwnedRows(OctopusImportPreviewPlanDto clientPlan, OctopusImportPreviewPlanDto serverPlan)
    {
        ArgumentNullException.ThrowIfNull(serverPlan);

        if (clientPlan == null) return serverPlan;

        var serverOwned = serverPlan.Resources.Where(IsServerOwned).GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var clientRows = clientPlan.Resources.Where(r => !IsHistoryRow(r)).Select(r => serverOwned.GetValueOrDefault(r.SourceId ?? string.Empty) ?? r).ToList();
        var clientIds = clientRows.Select(r => r.SourceId ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);

        clientPlan.Resources = clientRows.Concat(serverOwned.Values.Where(r => !clientIds.Contains(r.SourceId))).ToList();
        clientPlan.Summary = serverPlan.Summary;

        return clientPlan;
    }

    // A row the session's project selection leaves out is the server's too: a posted plan can never widen what is imported.
    private static bool IsServerOwned(OctopusImportResourceResultDto resource) => IsHistoryRow(resource) || resource.Choice != null || OctopusImportPreviewSummaries.IsOutsideSelection(resource);

    private static bool IsHistoryRow(OctopusImportResourceResultDto resource) => HistorySourceTypes.Contains(resource.SourceType ?? string.Empty);
}
