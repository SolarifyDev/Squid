using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.Deployments.Validation.Rules;

public sealed class SkipActionsValidationRule : IDeploymentValidationRule
{
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;

    public SkipActionsValidationRule(
        IReleaseDataProvider releaseDataProvider,
        IDeploymentSnapshotService deploymentSnapshotService)
    {
        _releaseDataProvider = releaseDataProvider;
        _deploymentSnapshotService = deploymentSnapshotService;
    }

    public int Order => 400;

    public bool Supports(DeploymentValidationStage stage) =>
        stage == DeploymentValidationStage.Precheck || stage == DeploymentValidationStage.Create;

    public async Task EvaluateAsync(DeploymentValidationContext context, DeploymentValidationReport report, CancellationToken cancellationToken = default)
    {
        if (context.SkipActionIds.Count == 0)
            return;

        var release = await _releaseDataProvider
            .GetReleaseByIdAsync(context.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null || release.ProjectDeploymentProcessSnapshotId <= 0)
            return;

        var processSnapshot = await _deploymentSnapshotService
            .LoadProcessSnapshotAsync(release.ProjectDeploymentProcessSnapshotId, cancellationToken).ConfigureAwait(false);

        var steps = ProcessSnapshotStepConverter.Convert(processSnapshot);
        var allActions = steps.SelectMany(s => s.Actions).ToList();
        var knownActionIds = allActions.Select(a => a.Id).ToHashSet();

        var unknownActionIds = context.SkipActionIds
            .Where(id => !knownActionIds.Contains(id))
            .OrderBy(id => id)
            .ToList();

        if (unknownActionIds.Count > 0)
        {
            report.AddBlockingIssue(DeploymentValidationIssueCode.SkipActionNotFound, $"SkipActionIds contains unknown action IDs: {string.Join(", ", unknownActionIds)}.");
        }

        var runnableActions = CollectRunnableActions(steps, context.EnvironmentId, release.ChannelId);

        if (runnableActions.Count == 0)
        {
            report.AddBlockingIssue(DeploymentValidationIssueCode.NoRunnableActions, "There are no runnable actions for this release in the selected environment/channel.");
            
            return;
        }

        var runnableAfterSkip = runnableActions
            .Where(action => !context.SkipActionIds.Contains(action.Id)).ToList();

        if (runnableAfterSkip.Count == 0)
        {
            report.AddBlockingIssue(DeploymentValidationIssueCode.AllRunnableActionsSkipped, "SkipActionIds would skip all runnable actions. At least one action must remain.");
        }
    }

    private static List<DeploymentActionDto> CollectRunnableActions(List<DeploymentStepDto> steps, int environmentId, int channelId)
    {
        return steps
            .Where(step => !step.IsDisabled)
            .SelectMany(step => step.Actions)
            .Where(action => StepEligibilityEvaluator.ShouldExecuteAction(action, environmentId, channelId))
            .ToList();
    }
}
