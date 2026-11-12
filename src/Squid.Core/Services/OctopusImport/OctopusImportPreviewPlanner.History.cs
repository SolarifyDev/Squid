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

        if (prior != null) return prior;

        return context.ReleaseVersionExists(release, ReleaseVersion(release, resource))
            ? new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, null, ReleaseVersionExistsInSquid(resource))
            : new OctopusImportPriorDecision(OctopusImportPreviewAction.Create, null);
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

        var action = context.PriorFor(OctopusResourceKind.ServerTask, task.SourceId) == null
            ? OctopusImportPreviewAction.Update
            : OctopusImportPriorMappingPolicy.DecideHistoryUnit(stored, task.GetSource<OctopusServerTaskDto>());

        return new OctopusImportPriorDecision(action, stored.DestinationId);
    }

    // Runbook runs and system tasks have no deployment and are out of scope.
    private static OctopusImportPriorDecision DecideServerTask(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        if (!context.TryGetHistoryUnitAction(resource.SourceId, out var unitAction)) return new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, null);

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

    private static OctopusImportDiagnosticDto ReleaseVersionExistsInSquid(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.ReleaseVersionExistsInSquid, $"Release '{resource.Name}' already exists in Squid on this project channel but was not created by an Octopus import. It is kept as it is; this release and its deployments are skipped.", resource);

    private static OctopusImportDiagnosticDto DeploymentTaskMissing(OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.DeploymentTaskMissing, "This Octopus deployment is skipped because its server task is not in the import source.", resource);
}
