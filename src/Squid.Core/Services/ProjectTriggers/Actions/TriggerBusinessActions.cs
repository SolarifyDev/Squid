using System.Globalization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Actions;

public sealed class TriggerBusinessActions : ITriggerBusinessActions
{
    private readonly ITriggerActionDataProvider _dataProvider;
    private readonly IReleaseService _releases;
    private readonly IDeploymentService _deployments;
    private readonly IDeploymentSnapshotService _snapshots;
    private readonly TriggerReleasePackageResolver _packages;
    private readonly TriggerDeploymentHistory _history;

    public TriggerBusinessActions(
        ITriggerActionDataProvider dataProvider,
        IReleaseService releases,
        IDeploymentService deployments,
        IDeploymentSnapshotService snapshots,
        TriggerReleasePackageResolver packages,
        TriggerDeploymentHistory history)
    {
        _dataProvider = dataProvider;
        _releases = releases;
        _deployments = deployments;
        _snapshots = snapshots;
        _packages = packages;
        _history = history;
    }

    public async Task<TriggerActionResult> ExecuteAsync(
        ProjectTriggerDefinition definition, string actionType, TriggerActionPlan plan, CancellationToken ct)
    {
        ValidateExecution(definition, actionType, plan);

        var project = await _dataProvider.GetEnabledProjectAsync(definition.SpaceId, definition.ProjectId, ct)
            ?? throw new TriggerActionRejectedException("The trigger's Project is disabled or unavailable in this Space.");

        return await ExecuteActionAsync(project, definition, actionType, plan, ct);
    }

    private void ValidateExecution(ProjectTriggerDefinition definition, string actionType, TriggerActionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(plan);

        if (!_dataProvider.HasActiveTransaction)
            throw new InvalidOperationException("Trigger actions require the caller's business transaction.");

        if (definition.ActionType != actionType)
            throw new TriggerActionRejectedException("The requested action differs from the recorded trigger configuration.");
    }

    private async Task<TriggerActionResult> ExecuteActionAsync(
        Project project,
        ProjectTriggerDefinition definition,
        string actionType,
        TriggerActionPlan plan,
        CancellationToken ct)
    {
        switch (actionType)
        {
            case ProjectTriggerActions.CreateRelease:
                return new TriggerActionResult(
                    await CreateReleaseAsync(project, definition, plan, ct).ConfigureAwait(false));
            case ProjectTriggerActions.DeployNewRelease:
                return await DeployNewReleaseAsync(project, definition, plan, ct).ConfigureAwait(false);
            case ProjectTriggerActions.AutoDeploy:
            case ProjectTriggerActions.DeployLatestRelease:
            case ProjectTriggerActions.DeployLatestReleaseToEnvironment:
                return await DeployExistingReleaseAsync(definition, plan, ct).ConfigureAwait(false);
            default:
                throw new TriggerActionRejectedException("This trigger business action is unsupported.");
        }
    }

    private async Task<TriggerActionResult> DeployNewReleaseAsync(
        Project project, ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        await ValidateTargetsAsync(definition, plan, ct).ConfigureAwait(false);

        var releaseId = await CreateReleaseAsync(project, definition, plan, ct).ConfigureAwait(false);

        return await CreateDeploymentAsync(definition, plan, releaseId, ct).ConfigureAwait(false);
    }

    private async Task<TriggerActionResult> DeployExistingReleaseAsync(
        ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        await ValidateTargetsAsync(definition, plan, ct).ConfigureAwait(false);

        var releaseId = await RequireExistingReleaseAsync(definition, plan, ct).ConfigureAwait(false);

        return await CreateDeploymentAsync(definition, plan, releaseId, ct).ConfigureAwait(false);
    }

    private async Task<int> CreateReleaseAsync(
        Project project, ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        if (plan.ChannelId is not > 0 || plan.ReleaseId != null || plan.BaselineDeploymentId != null)
            throw new TriggerActionRejectedException("A new release requires a concrete Channel and cannot reference an existing release or baseline.");

        var channelId = plan.ChannelId.Value;
        var version = await TriggerReleaseVersion.AllocateAsync(
            _dataProvider, project, channelId, plan.Version, ct).ConfigureAwait(false);

        await _packages.ValidateAsync(definition, plan, ct).ConfigureAwait(false);

        var created = await _releases.CreateReleaseAsync(new CreateReleaseCommand
        {
            SpaceId = project.SpaceId,
            ProjectId = project.Id,
            ChannelId = channelId,
            Version = version,
            SelectedPackages = plan.SelectedPackages ?? []
        }, ct).ConfigureAwait(false);

        await ValidateCreatedSnapshotAsync(
            project, created.Release.ProjectDeploymentProcessSnapshotId, channelId, plan, ct).ConfigureAwait(false);

        return created.Release.Id;
    }

    private async Task ValidateCreatedSnapshotAsync(
        Project project, int snapshotId, int channelId, TriggerActionPlan plan, CancellationToken ct)
    {
        // ReleaseService takes the immutable snapshot. Abort the Run transaction if configuration changed.
        var snapshot = await _snapshots.LoadProcessSnapshotAsync(snapshotId, ct).ConfigureAwait(false);

        if (snapshot.OriginalProcessId != project.DeploymentProcessId)
            throw new TriggerActionRejectedException("The Project's deployment process changed during release creation.");

        TriggerPackageReferences.ValidateSnapshot(snapshot, channelId, plan.PackageReferenceFingerprint);
    }

    private async Task<int> RequireExistingReleaseAsync(
        ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        if (plan.ReleaseId is not > 0
            || (definition.ActionType == ProjectTriggerActions.DeployLatestReleaseToEnvironment
                && plan.ChannelId is not > 0))
            throw new TriggerActionRejectedException("The deployment requires a concrete release and its configured Channel.");

        var exists = await _dataProvider.ReleaseExistsAsync(
            definition.SpaceId, definition.ProjectId, plan.ReleaseId.Value, plan.ChannelId, ct).ConfigureAwait(false);

        if (!exists)
            throw new TriggerActionRejectedException("The frozen release or its Channel is unavailable in the trigger's Project and Space.");

        await ValidateBaselineAsync(definition, plan, ct).ConfigureAwait(false);

        return plan.ReleaseId.Value;
    }

    private async Task ValidateBaselineAsync(
        ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        if (definition.ActionType is not
            (ProjectTriggerActions.AutoDeploy or ProjectTriggerActions.DeployLatestRelease)) return;

        var source = plan.SourceEnvironmentId ?? plan.EnvironmentId.Value;
        var baseline = await _history.FindSuccessfulAsync(
            definition.SpaceId,
            definition.ProjectId,
            source,
            plan.ChannelId,
            plan.BaselineDeploymentId,
            ct).ConfigureAwait(false);

        if (baseline == null || baseline.ReleaseId != plan.ReleaseId)
            throw new TriggerActionRejectedException("The selected release has no matching successful baseline deployment in the source environment.");
    }

    private async Task ValidateTargetsAsync(
        ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        ValidateEnvironmentPlan(definition, plan);

        var environmentIds = new[] { plan.EnvironmentId.Value, plan.SourceEnvironmentId ?? plan.EnvironmentId.Value }
            .Distinct().ToArray();
        var count = await _dataProvider.CountEnvironmentsAsync(
            definition.SpaceId, environmentIds, ct).ConfigureAwait(false);

        if (count != environmentIds.Length)
            throw new TriggerActionRejectedException("An environment is unavailable in the trigger's Space.");

        await ValidateMachinesAsync(definition, plan, ct).ConfigureAwait(false);
    }

    private static void ValidateEnvironmentPlan(ProjectTriggerDefinition definition, TriggerActionPlan plan)
    {
        if (plan.EnvironmentId is not > 0
            || (definition.ActionType == ProjectTriggerActions.DeployLatestRelease
                && plan.SourceEnvironmentId is not > 0))
            throw new TriggerActionRejectedException("The deployment plan is missing a required environment.");

        if (definition.ActionType == ProjectTriggerActions.AutoDeploy
            && plan.SourceEnvironmentId.HasValue && plan.SourceEnvironmentId != plan.EnvironmentId)
            throw new TriggerActionRejectedException("A target catch-up requires its source and destination to be the same environment.");

        if (definition.ActionType == ProjectTriggerActions.DeployLatestReleaseToEnvironment
            && plan.BaselineDeploymentId.HasValue)
            throw new TriggerActionRejectedException("Deploying a Channel's selected release does not accept a baseline deployment.");
    }

    private async Task ValidateMachinesAsync(
        ProjectTriggerDefinition definition, TriggerActionPlan plan, CancellationToken ct)
    {
        var ids = plan.SpecificMachineIds?.Distinct().ToArray() ?? [];
        var catchup = definition.ActionType == ProjectTriggerActions.AutoDeploy;

        if ((catchup && ids.Length == 0) || ids.Any(id => id <= 0))
            throw new TriggerActionRejectedException("A machine-scoped deployment requires a nonempty list of valid target machine IDs.");

        if (ids.Length == 0) return;

        var machines = await _dataProvider.GetEnabledMachinesAsync(definition.SpaceId, ids, ct).ConfigureAwait(false);

        if (machines.Count != ids.Length
            || machines.Any(m => !DeploymentTargetFinder.ParseIds(m.EnvironmentIds).Contains(plan.EnvironmentId.Value)
                                || (catchup && m.HealthStatus != MachineHealthStatus.Healthy)))
            throw new TriggerActionRejectedException("A target machine is unavailable in the destination environment or no longer meets the required health state.");
    }

    private async Task<TriggerActionResult> CreateDeploymentAsync(
        ProjectTriggerDefinition definition, TriggerActionPlan plan, int releaseId, CancellationToken ct)
    {
        var result = await _deployments.CreateDeploymentForTriggerAsync(new CreateDeploymentCommand
        {
            SpaceId = definition.SpaceId,
            ReleaseId = releaseId,
            EnvironmentId = plan.EnvironmentId.Value,
            SpecificMachineIds = (plan.SpecificMachineIds ?? [])
                .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(),
            SkipActionIds = plan.SkipActionIds ?? [],
            UseGuidedFailure = plan.UseGuidedFailure
        }, ct).ConfigureAwait(false);

        return new TriggerActionResult(releaseId, result.Deployment.Id, result.TaskId);
    }
}
