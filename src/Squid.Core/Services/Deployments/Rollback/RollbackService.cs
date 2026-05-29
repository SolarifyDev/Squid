using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Rollback.Exceptions;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Rollback;

/// <summary>
/// PR-12/13 — rollback for an environment. Resolves the release to roll back
/// to (the prior successful release, or an operator-specified prior release)
/// from the deployment journal, then re-deploys it. A rollback is a normal
/// deployment of an older release, so the action delegates to
/// <see cref="IDeploymentService.CreateDeploymentAsync"/> for all validation,
/// snapshotting and enqueueing — no parallel deploy path.
/// </summary>
public interface IRollbackService : IScopedDependency
{
    /// <summary>
    /// The release to roll back to for <paramref name="projectId"/> in
    /// <paramref name="environmentId"/>, or <see langword="null"/> when there
    /// is no prior distinct release. Read-only — does not deploy.
    /// </summary>
    Task<RollbackReleaseHistoryEntry?> GetRollbackTargetAsync(int projectId, int environmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Roll <paramref name="command"/>'s environment back to a prior release
    /// (auto-resolved previous, or the command's explicit <c>ReleaseId</c>),
    /// re-deploying it through the standard deployment pipeline.
    /// </summary>
    Task<DeploymentCreatedEvent> RollbackDeploymentAsync(RollbackDeploymentCommand command, CancellationToken cancellationToken = default);
}

public class RollbackService(
    IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
    IDeploymentService deploymentService) : IRollbackService
{
    public async Task<RollbackReleaseHistoryEntry?> GetRollbackTargetAsync(int projectId, int environmentId, CancellationToken cancellationToken = default)
    {
        var history = await deploymentCompletionDataProvider
            .GetSuccessfulReleaseHistoryAsync(projectId, environmentId, cancellationToken).ConfigureAwait(false);

        return RollbackTargetSelector.SelectPreviousDistinctRelease(history);
    }

    public async Task<DeploymentCreatedEvent> RollbackDeploymentAsync(RollbackDeploymentCommand command, CancellationToken cancellationToken = default)
    {
        var targetReleaseId = await ResolveTargetReleaseIdAsync(command.ProjectId, command.EnvironmentId, command.ReleaseId, cancellationToken).ConfigureAwait(false);

        var createCommand = BuildCreateCommand(command, targetReleaseId);

        return await deploymentService.CreateDeploymentAsync(createCommand, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve + validate the effective rollback target. When
    /// <paramref name="requestedReleaseId"/> is null, auto-selects the previous
    /// distinct release; otherwise validates the requested release actually
    /// deployed successfully to the environment and is not the current one.
    /// Throws <see cref="RollbackNotAvailableException"/> when no valid target
    /// exists.
    /// </summary>
    private async Task<int> ResolveTargetReleaseIdAsync(int projectId, int environmentId, int? requestedReleaseId, CancellationToken cancellationToken)
    {
        var history = await deploymentCompletionDataProvider
            .GetSuccessfulReleaseHistoryAsync(projectId, environmentId, cancellationToken).ConfigureAwait(false);

        if (history.Count == 0)
            throw new RollbackNotAvailableException(projectId, environmentId, "no successful deployment exists for this environment.");

        var currentReleaseId = history[0].ReleaseId;

        if (requestedReleaseId is null)
        {
            var previous = RollbackTargetSelector.SelectPreviousDistinctRelease(history);

            if (previous is null)
                throw new RollbackNotAvailableException(projectId, environmentId, "no prior release differs from the currently deployed one.");

            return previous.ReleaseId;
        }

        var requested = requestedReleaseId.Value;

        if (requested == currentReleaseId)
            throw new RollbackNotAvailableException(projectId, environmentId, $"release {requested} is already the current release.");

        if (history.All(entry => entry.ReleaseId != requested))
            throw new RollbackNotAvailableException(projectId, environmentId, $"release {requested} has no successful deployment to this environment to roll back to.");

        return requested;
    }

    private static CreateDeploymentCommand BuildCreateCommand(RollbackDeploymentCommand command, int targetReleaseId)
        => new()
        {
            SpaceId = command.SpaceId,
            ReleaseId = targetReleaseId,
            EnvironmentId = command.EnvironmentId,
            Name = command.Name ?? $"Rollback to release {targetReleaseId}",
            Comments = command.Comments,
            ForcePackageDownload = command.ForcePackageDownload,
            ForcePackageRedeployment = command.ForcePackageRedeployment,
            UseGuidedFailure = command.UseGuidedFailure,
            QueueTime = command.QueueTime,
            QueueTimeExpiry = command.QueueTimeExpiry,
            SpecificMachineIds = command.SpecificMachineIds,
            ExcludedMachineIds = command.ExcludedMachineIds,
            SkipActionIds = command.SkipActionIds
        };
}
