using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The incremental view of a plan: what each Update / Fill writes (old → new, secrets named only), why each skipped resource is skipped,
/// and the plan-level summary (<see cref="OctopusImportPreviewSummaries"/>). Runs after every decision is final.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private static void AddChanges(IReadOnlyList<OctopusResourceNode> nodes, IReadOnlyList<OctopusImportResourceResultDto> previews, PreviewPlanningContext context)
    {
        var previewsById = previews.ToDictionary(p => p.SourceId, StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            var preview = previewsById[node.SourceId];

            if (preview.PreviewAction != OctopusImportPreviewAction.Update) continue;

            if (node.Kind == OctopusResourceKind.Release) AddReleaseChanges(node, preview, context);
            if (node.Kind == OctopusResourceKind.Deployment) AddHistoryUnitChanges(node, preview, previewsById, context);
        }
    }

    // Squid's notes are read only for a release whose notes changed in Octopus: a release that is only moved onto a re-created owner lists just that.
    private static void AddReleaseChanges(OctopusResourceNode release, OctopusImportResourceResultDto preview, PreviewPlanningContext context)
    {
        var notesRead = preview.DestinationId is { } releaseId && context.Conflicts.StoredHistory.ReleaseNotes.ContainsKey(releaseId);
        var changes = !IsRelinked(preview) || notesRead ? OctopusImportPreviewChanges.ForRelease(release.GetSource<OctopusReleaseDto>(), preview.DestinationId, context.Conflicts.StoredHistory) : [];

        preview.Changes = IsRelinked(preview) ? OctopusImportPreviewChanges.Relinked(changes.Where(c => c.Field != OctopusImportPreviewChanges.RecordField)) : changes;
    }

    private static bool IsRelinked(OctopusImportResourceResultDto preview) => preview.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryRelinked);

    // A deployment and its task are one unit: both name the same written fields.
    private static void AddHistoryUnitChanges(OctopusResourceNode deployment, OctopusImportResourceResultDto preview, IReadOnlyDictionary<string, OctopusImportResourceResultDto> previewsById, PreviewPlanningContext context)
    {
        var task = context.FindNode(deployment.GetSource<OctopusDeploymentDto>()?.TaskId, OctopusResourceKind.ServerTask);
        var storedUnit = context.PriorFor(OctopusResourceKind.Deployment, deployment.SourceId);
        var storedTaskId = task == null ? null : context.PriorFor(OctopusResourceKind.ServerTask, task.SourceId)?.DestinationId;

        var changes = OctopusImportPreviewChanges.ForHistoryUnit(task?.GetSource<OctopusServerTaskDto>(), storedUnit, storedTaskId, context.Conflicts.StoredHistory);
        var taskRead = storedTaskId == null || context.Conflicts.StoredHistory.Tasks.ContainsKey(storedTaskId.Value);

        preview.Changes = IsRelinked(preview) ? OctopusImportPreviewChanges.Relinked(taskRead ? changes.Where(c => c.Field != OctopusImportPreviewChanges.RecordField) : []) : changes;

        if (task != null && previewsById.TryGetValue(task.SourceId, out var taskPreview) && taskPreview.PreviewAction == OctopusImportPreviewAction.Update)
            taskPreview.Changes = preview.Changes.ToList();
    }

    // A task document need not name its project: it belongs to the project of the deployment it is one history unit with.
    private static void AttributeTasksToTheirDeploymentProject(IReadOnlyList<OctopusResourceNode> nodes, IReadOnlyList<OctopusImportResourceResultDto> previews)
    {
        var previewsById = previews.ToDictionary(p => p.SourceId, StringComparer.OrdinalIgnoreCase);

        foreach (var deployment in nodes.Where(n => n.Kind == OctopusResourceKind.Deployment && !string.IsNullOrWhiteSpace(n.OwnerProjectId)))
        {
            var taskId = deployment.GetSource<OctopusDeploymentDto>()?.TaskId;

            if (string.IsNullOrWhiteSpace(taskId) || !previewsById.TryGetValue(taskId, out var task)) continue;
            if (task.SourceType != nameof(OctopusResourceKind.ServerTask) || !string.IsNullOrWhiteSpace(task.OwnerProjectSourceId)) continue;

            task.OwnerProjectSourceId = deployment.OwnerProjectId;
        }
    }

    private static void AddSkipReasons(IEnumerable<OctopusImportResourceResultDto> previews)
    {
        foreach (var preview in previews)
            preview.SkipReason = OctopusImportPreviewSummaries.SkipReasonOf(preview);
    }

    private static OctopusImportPreviewSummaryDto Summary(OctopusImportPreviewPlanDto plan, PreviewPlanningContext context)
        => OctopusImportPreviewSummaries.Build(plan.Resources, context.Conflicts.Destinations, context.Prior, plan.HasBlockers);
}
