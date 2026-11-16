using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// History decisions (Release, Deployment, ServerTask). A Deployment and its ServerTask are one history
/// unit: the deployment decides, the task follows. History whose owners do not map to Squid rows is
/// skipped with a warning instead of blocking the configuration import.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private static bool IsHistoryKind(OctopusResourceKind kind)
        => kind is OctopusResourceKind.Release or OctopusResourceKind.Deployment or OctopusResourceKind.ServerTask;

    private static OctopusImportPriorDecision DecideHistory(OctopusResourceNode resource, PreviewPlanningContext context)
        => resource.Kind switch
        {
            OctopusResourceKind.Release => DecideRelease(resource, context),
            OctopusResourceKind.Deployment => DecideDeployment(resource, context),
            _ => DecideServerTask(resource, context)
        };

    private static OctopusImportPriorDecision DecideRelease(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        var release = resource.GetSource<OctopusReleaseDto>();

        if (!context.Resolves(OctopusResourceKind.Project, release?.ProjectId) || !context.Resolves(OctopusResourceKind.Channel, release?.ChannelId)) return OwnerNotImported(resource);

        var prior = OctopusImportPriorMappingPolicy.Decide(resource, context.Prior, context.PreviewActionOf);

        if (prior != null) return RelinkOrphanedRelease(resource, release, KeepNotesEditedInSquid(resource, prior, context) ?? prior, context);

        return context.ReleaseVersionExists(release, ReleaseVersion(release, resource))
            ? new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, null, ReleaseVersionExistsInSquid(resource))
            : new OctopusImportPriorDecision(OctopusImportPreviewAction.Create, null);
    }

    // A variable's row goes stale whenever its set is saved in Squid (the variables are re-inserted with new ids): one the Squid set
    // still holds by name is in Squid, not "not in the earlier import".
    private static OctopusImportPriorDecision KeepVariableStillInSquid(OctopusResourceNode resource, OctopusImportPriorDecision decision, PreviewPlanningContext context)
    {
        if (resource.Kind != OctopusResourceKind.Variable || decision.Action != OctopusImportPreviewAction.Skip) return decision;

        var setId = context.PriorFor(OctopusResourceKind.VariableSet, OctopusImportPriorMappingPolicy.OwnerSourceId(resource))?.DestinationId;
        var name = resource.GetSource<OctopusVariableDto>()?.Name;

        return setId != null && context.Conflicts.DestinationVariables.Contains(new OctopusImportDestinationVariable(setId.Value, name)) ? new OctopusImportPriorDecision(OctopusImportPreviewAction.AlreadyImported, null) : decision;
    }

    // An Update whose Squid notes were edited since the import is skipped: the release stays linked (its deployments land on it), nothing is written.
    private static OctopusImportPriorDecision KeepNotesEditedInSquid(OctopusResourceNode resource, OctopusImportPriorDecision prior, PreviewPlanningContext context)
    {
        if (prior.Action != OctopusImportPreviewAction.Update || prior.DestinationId is not { } releaseId) return null;

        var squidNotes = context.Conflicts.StoredHistory.ReleaseNotes.GetValueOrDefault(releaseId);

        if (!OctopusImportPriorMappingPolicy.ReleaseNotesEditedInSquid(context.PriorFor(OctopusResourceKind.Release, resource.SourceId), squidNotes)) return null;

        return new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, releaseId, ReleaseNotesEditedInSquid(resource));
    }

    private static OctopusImportPriorDecision DecideDeployment(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        var deployment = resource.GetSource<OctopusDeploymentDto>();

        if (!DeploymentOwnersResolve(deployment, context)) return OwnerNotImported(resource);

        var task = context.FindNode(deployment.TaskId, OctopusResourceKind.ServerTask);

        if (task == null) return new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, null, DeploymentTaskMissing(resource));

        var decision = DecideHistoryUnit(resource, task, context);

        context.RecordHistoryUnit(task.SourceId, decision.Action);

        return decision;
    }

    // A stored deployment whose task row is gone cannot be "already imported": the unit is rewritten.
    private static OctopusImportPriorDecision DecideHistoryUnit(OctopusResourceNode deployment, OctopusResourceNode task, PreviewPlanningContext context)
    {
        var stored = context.PriorFor(OctopusResourceKind.Deployment, deployment.SourceId);

        if (stored == null) return new OctopusImportPriorDecision(OctopusImportPreviewAction.Create, null);

        var storedTask = context.PriorFor(OctopusResourceKind.ServerTask, task.SourceId);
        var action = storedTask == null ? OctopusImportPreviewAction.Update : OctopusImportPriorMappingPolicy.DecideHistoryUnit(stored, task.GetSource<OctopusServerTaskDto>());
        var decision = KeepNewerHistory(deployment, task, storedTask, new OctopusImportPriorDecision(action, stored.DestinationId), context);
        var source = deployment.GetSource<OctopusDeploymentDto>();

        var orphaned = context.Conflicts.OrphanedHistory.DeploymentIds.Contains(stored.DestinationId);

        return RelinkToRecreatedOwners(deployment, decision, orphaned, context, source.ProjectId, source.ChannelId, source.ReleaseId, source.EnvironmentId);
    }

    // An older copy of a task Squid holds finished (the importer's own rule) is not an update: the newer imported history is kept.
    private static OctopusImportPriorDecision KeepNewerHistory(OctopusResourceNode deployment, OctopusResourceNode task, OctopusImportPriorMapping storedTask, OctopusImportPriorDecision decision, PreviewPlanningContext context)
    {
        if (decision.Action != OctopusImportPreviewAction.Update || storedTask == null) return decision;

        var squidTask = context.Conflicts.StoredHistory.Tasks.GetValueOrDefault(storedTask.DestinationId);

        if (squidTask == null || !OctopusImportHistoryWriteRules.IsOlderCopy(squidTask.State, task.GetSource<OctopusServerTaskDto>())) return decision;

        return new OctopusImportPriorDecision(OctopusImportPreviewAction.AlreadyImported, decision.DestinationId, OlderHistoryKept(deployment));
    }

    // Squid deletes do not cascade: history whose project, channel, release or environment was deleted in Squid is still there. When this
    // plan creates that owner again (or links it to a Squid row by name), the history is moved onto it, never imported a second time.
    // History whose owners all exist is never moved, whatever Octopus did to it since (a release moved to another channel stays put).
    private static OctopusImportPriorDecision RelinkToRecreatedOwners(OctopusResourceNode resource, OctopusImportPriorDecision decision, bool orphaned, PreviewPlanningContext context, params string[] ownerSourceIds)
    {
        if (!orphaned || decision.DestinationId == null || !ownerSourceIds.Any(id => IsNewDestination(context.PreviewActionOf(id)))) return decision;

        return new OctopusImportPriorDecision(OctopusImportPreviewAction.Update, decision.DestinationId, HistoryRelinked(resource));
    }

    // A release is moved only onto a project channel that does not already hold its version (one linked by name may): it stays where it is.
    private static OctopusImportPriorDecision RelinkOrphanedRelease(OctopusResourceNode resource, OctopusReleaseDto release, OctopusImportPriorDecision decision, PreviewPlanningContext context)
    {
        var orphaned = decision.DestinationId is { } releaseId && context.Conflicts.OrphanedHistory.ReleaseIds.Contains(releaseId);

        if (orphaned && context.ReleaseVersionExists(release, ReleaseVersion(release, resource))) return decision with { Diagnostic = decision.Diagnostic ?? RelinkTargetHoldsTheVersion(resource) };

        return RelinkToRecreatedOwners(resource, decision, orphaned, context, release.ProjectId, release.ChannelId);
    }

    private static bool IsNewDestination(OctopusImportPreviewAction? action) => action is OctopusImportPreviewAction.Create or OctopusImportPreviewAction.ReuseExisting;

    // What an Update was previewed against, so confirmation can refuse it once a later import has moved the record on.
    private static string PreviewedBasis(OctopusResourceNode resource, OctopusImportPreviewAction action, PreviewPlanningContext context)
        => action == OctopusImportPreviewAction.Update ? OctopusImportPriorMappingPolicy.ImportBasis(context.PriorFor(resource.Kind, resource.SourceId)) : null;

    // Runbook runs and system tasks have no deployment and are out of scope.
    private static OctopusImportPriorDecision DecideServerTask(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        if (!context.TryGetHistoryUnitAction(resource.SourceId, out var unitAction)) return new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, null, TaskWithoutImportedDeployment(resource));

        var destinationId = unitAction == OctopusImportPreviewAction.Create ? null : context.PriorFor(OctopusResourceKind.ServerTask, resource.SourceId)?.DestinationId;

        return new OctopusImportPriorDecision(unitAction, destinationId);
    }

    private static bool DeploymentOwnersResolve(OctopusDeploymentDto deployment, PreviewPlanningContext context)
        => context.Resolves(OctopusResourceKind.Project, deployment?.ProjectId)
           && context.Resolves(OctopusResourceKind.Channel, deployment?.ChannelId)
           && context.Resolves(OctopusResourceKind.Environment, deployment?.EnvironmentId)
           && context.Resolves(OctopusResourceKind.Release, deployment?.ReleaseId);

    // Same version rule confirmation uses when it creates the release.
    private static string ReleaseVersion(OctopusReleaseDto release, OctopusResourceNode resource)
        => string.IsNullOrWhiteSpace(release.Version) ? resource.Name : release.Version;

    // Decisions only ever write channels and history, which carry no manual-configuration markers.
    private static void ApplyDecision(OctopusImportResourceResultDto result, OctopusImportPriorDecision decision)
    {
        result.PreviewAction = decision.Action;
        result.DestinationId = decision.DestinationId;

        if (decision.Diagnostic != null) result.Diagnostics.Add(decision.Diagnostic);

        if (decision.Action == OctopusImportPreviewAction.Skip) result.OutcomeState = OctopusImportResourceOutcomeState.Skipped;
    }

    private static OctopusImportPriorDecision OwnerNotImported(OctopusResourceNode resource)
        => new(OctopusImportPreviewAction.Skip, null, Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.HistoryOwnerNotImported, $"This Octopus {resource.Kind} is skipped because its project, channel, environment or release is neither imported by this plan nor by an earlier import.", resource));

    private static OctopusImportDiagnosticDto RelinkTargetHoldsTheVersion(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.ReleaseVersionExistsInSquid, $"Release '{resource.Name}' lost its project or channel to a Squid delete, and the channel it would move onto already holds this version in Squid. It is kept where it is and not moved.", resource);

    private static OctopusImportDiagnosticDto ReleaseVersionExistsInSquid(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.ReleaseVersionExistsInSquid, $"Release '{resource.Name}' already exists in Squid on this project channel but was not created by an Octopus import. It is kept as it is; this release and its deployments are skipped.", resource);

    private static OctopusImportDiagnosticDto ReleaseNotesEditedInSquid(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ReleaseNotesEditedInSquid, $"The notes of release '{resource.Name}' were edited in Squid after the import; they are kept and the Octopus notes are not applied.", resource);

    private static OctopusImportDiagnosticDto HistoryRelinked(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.HistoryRelinked, $"This Octopus {resource.Kind} is still in Squid, but something it belongs to (project, channel, release or environment) was deleted in Squid and is imported again: it is moved onto that, never imported a second time.", resource);

    private static OctopusImportDiagnosticDto OlderHistoryKept(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.OlderHistoryKept, "Squid holds this Octopus deployment finished, but the import source has it still running: the source is an older copy, so the newer imported history is kept.", resource);

    private static OctopusImportDiagnosticDto TaskWithoutImportedDeployment(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.TaskImportedWithDeployment, "An Octopus task is imported only together with its deployment; this one has no deployment this import imports (a runbook run, a system task, or a skipped deployment).", resource);

    private static OctopusImportDiagnosticDto DeploymentTaskMissing(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.DeploymentTaskMissing, "This Octopus deployment is skipped because its server task is not in the import source.", resource);
}
