using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// History execution. A Deployment and its ServerTask are one unit written by the deployment (the task node only
/// follows it). Updates touch only rows an earlier import recorded in the source map, re-checked under the space lock.
/// </summary>
public sealed partial class OctopusImportConfirmationOrchestrator
{
    private sealed record HistoryOwners(int ProjectId, int ChannelId, int ReleaseId, int EnvironmentId);

    private sealed record HistoryUnitSource(OctopusResourceNode Deployment, OctopusDeploymentDto DeploymentDto, OctopusResourceNode Task, OctopusServerTaskDto TaskDto, OctopusTaskHistoryDetails Details, string DetailFingerprint);

    private async Task CreateHistoryUnitAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, CancellationToken ct)
    {
        if (resource.Kind == OctopusResourceKind.ServerTask)
        {
            execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Created);
            return;
        }

        var source = await LoadHistoryUnitSourceAsync(execution, resource, ct).ConfigureAwait(false);
        var unit = BuildHistoryUnit(execution, source, null, null);

        var result = await _deploymentHistoryImporter.UpsertAsync(unit, ct).ConfigureAwait(false);

        execution.AddCreated(resource, result.DeploymentId);
        execution.AddCreated(source.Task, result.TaskId);
        execution.HistoryFingerprints[resource.SourceId] = (OctopusImportFingerprint.ForTask(source.TaskDto), source.DetailFingerprint);
    }

    private async Task UpdateResourceAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportResourceResultDto preview, CancellationToken ct)
    {
        try
        {
            switch (resource.Kind)
            {
                case OctopusResourceKind.Release:
                    await UpdateReleaseAsync(execution, resource, preview, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Deployment:
                    await UpdateHistoryUnitAsync(execution, resource, preview, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.ServerTask:
                    return;
                default:
                    RejectUpdate(execution, resource);
                    return;
            }
        }
        catch (Exception ex) when (!IsExplained(ex))
        {
            Log.Error(ex, "Octopus import confirmation failed while updating {ResourceKind} {ResourceName} ({ResourceId}).", resource.Kind, resource.Name, resource.SourceId);

            execution.MarkFailed(resource, Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportConfirmationDiagnosticCodes.ResourceExecutionFailed, $"Octopus import confirmation failed while updating {resource.Kind} '{resource.Name}'.", resource));

            throw;
        }
    }

    // A release update writes only its notes (version and packages are never re-applied).
    private async Task UpdateReleaseAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportResourceResultDto preview, CancellationToken ct)
    {
        var destinationId = EnsurePriorDestination(execution, resource, preview.DestinationId);
        var source = resource.GetSource<OctopusReleaseDto>() ?? throw new OctopusImportConfirmationException("Release source payload is missing.");

        await _deploymentHistoryImporter.UpdateReleaseNotesAsync(destinationId, source.ReleaseNotes, ct).ConfigureAwait(false);

        execution.AddReused(resource, destinationId);
        execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Updated);
    }

    private async Task UpdateHistoryUnitAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportResourceResultDto preview, CancellationToken ct)
    {
        var deploymentId = EnsurePriorDestination(execution, resource, preview.DestinationId);
        var source = await LoadHistoryUnitSourceAsync(execution, resource, ct).ConfigureAwait(false);
        var stored = execution.PriorFor(resource);
        var existingTaskId = execution.PriorFor(source.Task)?.DestinationId;

        var scalarChanged = OctopusImportHistoryWriteRules.ScalarChanged(stored, source.TaskDto, existingTaskId != null);
        var replaceChildren = OctopusImportHistoryWriteRules.ReplaceChildren(stored, source.DetailFingerprint, scalarChanged, existingTaskId != null);
        var unit = BuildHistoryUnit(execution, source, deploymentId, existingTaskId) with { ScalarChanged = scalarChanged, ReplaceChildren = replaceChildren };

        var result = await _deploymentHistoryImporter.UpsertAsync(unit, ct).ConfigureAwait(false);

        RecordUpdatedUnit(execution, source, result, existingTaskId, replaceChildren);
    }

    // An updated unit maps as Reused (it existed before); a task whose row was gone was re-created.
    private static void RecordUpdatedUnit(ConfirmationExecutionContext execution, HistoryUnitSource source, OctopusImportDeploymentHistoryResult result, int? existingTaskId, bool childrenReplaced)
    {
        execution.AddReused(source.Deployment, result.DeploymentId);
        execution.MarkOutcome(source.Deployment, OctopusImportResourceOutcomeState.Updated);

        if (existingTaskId == null)
            execution.AddCreated(source.Task, result.TaskId);
        else
        {
            execution.AddReused(source.Task, result.TaskId);
            execution.MarkOutcome(source.Task, OctopusImportResourceOutcomeState.Updated);
        }

        execution.HistoryFingerprints[source.Deployment.SourceId] = (OctopusImportFingerprint.ForTask(source.TaskDto), childrenReplaced ? source.DetailFingerprint : null);

        if (childrenReplaced && source.DetailFingerprint == null) execution.HistoryDetailsCleared.Add(source.Deployment.SourceId);
    }

    private async Task<HistoryUnitSource> LoadHistoryUnitSourceAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, CancellationToken ct)
    {
        var deployment = resource.GetSource<OctopusDeploymentDto>() ?? throw new OctopusImportConfirmationException("Deployment source payload is missing.");
        var taskResource = execution.FindResource(deployment.TaskId, OctopusResourceKind.ServerTask);
        var task = taskResource?.GetSource<OctopusServerTaskDto>() ?? throw new OctopusImportConfirmationException("Octopus deployment server task source payload is missing.");
        var details = await ReadHistoryDetailsAsync(execution, task.Id ?? taskResource.SourceId, ct).ConfigureAwait(false);

        return new HistoryUnitSource(resource, deployment, taskResource, task, details, IncomingDetailFingerprint(task, details));
    }

    // The fetch's own fingerprint is what the preview compared, so it is the one recorded; it is trusted only with the details beside it.
    private static string IncomingDetailFingerprint(OctopusServerTaskDto task, OctopusTaskHistoryDetails details)
    {
        if (details == null) return null;

        return string.IsNullOrWhiteSpace(task.DetailFingerprint) ? OctopusImportFingerprint.ForTaskDetails(details) : task.DetailFingerprint;
    }

    private async Task<OctopusTaskHistoryDetails> ReadHistoryDetailsAsync(ConfirmationExecutionContext execution, string taskId, CancellationToken ct)
    {
        if (_historyDetailsStore == null || string.IsNullOrWhiteSpace(execution.Request.SessionDirectory)) return null;

        return await _historyDetailsStore.ReadAsync(execution.Request.SessionDirectory, taskId, ct).ConfigureAwait(false);
    }

    private static OctopusImportHistoryUnit BuildHistoryUnit(ConfirmationExecutionContext execution, HistoryUnitSource source, int? existingDeploymentId, int? existingTaskId)
    {
        var owners = ResolveHistoryOwners(execution, source.Deployment, source.DeploymentDto);

        return new OctopusImportHistoryUnit
        {
            SpaceId = execution.DestinationSpaceId, ProjectId = owners.ProjectId, ChannelId = owners.ChannelId, ReleaseId = owners.ReleaseId, EnvironmentId = owners.EnvironmentId,
            ExistingDeploymentId = existingDeploymentId, ExistingTaskId = existingTaskId,
            Deployment = source.DeploymentDto, Task = source.TaskDto, Details = source.Details,
            TeamResolver = teamId => execution.IdMap.TryGetDestinationId(teamId, OctopusResourceKind.Team.ToString(), out var squidTeamId) ? squidTeamId : null,
            MachineNames = execution.MachineNames
        };
    }

    private static HistoryOwners ResolveHistoryOwners(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusDeploymentDto deployment)
        => new(ResolveOwner(execution, resource, deployment.ProjectId, OctopusResourceKind.Project), ResolveOwner(execution, resource, deployment.ChannelId, OctopusResourceKind.Channel), ResolveOwner(execution, resource, deployment.ReleaseId, OctopusResourceKind.Release), ResolveOwner(execution, resource, deployment.EnvironmentId, OctopusResourceKind.Environment));

    private static int ResolveOwner(ConfirmationExecutionContext execution, OctopusResourceNode resource, string ownerSourceId, OctopusResourceKind ownerKind)
    {
        if (execution.IdMap.TryGetDestinationId(ownerSourceId, ownerKind.ToString(), out var destinationId)) return destinationId;

        throw MappingBlocked(resource, $"Octopus deployment {ownerKind.ToString().ToLowerInvariant()} '{ownerSourceId}' has not been mapped.");
    }

    // The plan was validated before the space lock was taken: a create whose source an earlier (or concurrent) import has
    // recorded since would duplicate it. The task follows its deployment, which may re-create it under a stale task row.
    private static void EnsureCreateNotStale(ConfirmationExecutionContext execution, OctopusResourceNode resource)
    {
        if (resource.Kind == OctopusResourceKind.ServerTask || execution.PriorFor(resource) == null) return;

        throw StalePlan(execution, resource, "This resource was imported by another confirmation after this plan was previewed. Re-run the preview.");
    }

    // The plan is client-posted: an update's destination id is trusted only when the source map recorded exactly that id.
    private static int EnsurePriorDestination(ConfirmationExecutionContext execution, OctopusResourceNode resource, int? destinationId)
    {
        if (destinationId is > 0 && execution.IdMap.IsPriorDestination(resource, destinationId.Value)) return destinationId.Value;

        throw StalePlan(execution, resource, "This resource is marked for update, but no earlier import of it into this destination is recorded. Re-run the preview.");
    }

    private static OctopusImportStalePlanException StalePlan(ConfirmationExecutionContext execution, OctopusResourceNode resource, string message)
    {
        execution.MarkFailed(resource, Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportPreviewDiagnosticCodes.StalePreviewPlan, message, resource));

        return new OctopusImportStalePlanException(resource.Kind.ToString(), resource.SourceId);
    }

    // Failures already recorded on the resource with their own diagnostic are not re-labelled as execution failures.
    private static bool IsExplained(Exception exception) => exception is OctopusImportConfirmationException or OctopusImportStalePlanException;
}
