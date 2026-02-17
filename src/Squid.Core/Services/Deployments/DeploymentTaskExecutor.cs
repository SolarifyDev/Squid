using Halibut;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Settings.GithubPackage;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentTaskExecutor : IScopedDependency
{
    Task ProcessAsync(int serverTaskId, CancellationToken ct);
}

public partial class DeploymentTaskExecutor : IDeploymentTaskExecutor
{
    private readonly HalibutRuntime _halibutRuntime;
    private readonly IYamlNuGetPacker _yamlNuGetPacker;
    private readonly IDeploymentPlanService _planService;
    private readonly IDeploymentTargetFinder _targetFinder;
    private readonly IDeploymentAccountDataProvider _deploymentAccountDataProvider;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IDeploymentVariableResolver _variableResolver;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IActionHandlerRegistry _actionHandlerRegistry;
    private readonly IEnumerable<IEndpointVariableContributor> _variableContributors;
    private readonly IEnumerable<IScriptContextWrapper> _scriptWrappers;
    private readonly CalamariGithubPackageSetting _calamariGithubPackageSetting;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;
    private readonly IServerTaskLogDataProvider _serverTaskLogDataProvider;
    private readonly IActivityLogDataProvider _activityLogDataProvider;

    private DeploymentTaskContext _ctx;
    private IEndpointVariableContributor _resolvedContributor;
    private long _logSequence;

    public DeploymentTaskExecutor(
        IYamlNuGetPacker yamlNuGetPacker,
        IDeploymentPlanService planService,
        IDeploymentTargetFinder targetFinder,
        IDeploymentAccountDataProvider deploymentAccountDataProvider,
        IReleaseDataProvider releaseDataProvider,
        IGenericDataProvider genericDataProvider,
        IDeploymentVariableResolver variableResolver,
        IServerTaskDataProvider serverTaskDataProvider,
        IDeploymentDataProvider deploymentDataProvider,
        IActionHandlerRegistry actionHandlerRegistry,
        IEnumerable<IEndpointVariableContributor> variableContributors,
        IEnumerable<IScriptContextWrapper> scriptWrappers,
        IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
        IServerTaskLogDataProvider serverTaskLogDataProvider,
        IActivityLogDataProvider activityLogDataProvider,
        HalibutRuntime halibutRuntime,
        CalamariGithubPackageSetting calamariGithubPackageSetting)
    {
        _planService = planService;
        _targetFinder = targetFinder;
        _halibutRuntime = halibutRuntime;
        _yamlNuGetPacker = yamlNuGetPacker;
        _variableResolver = variableResolver;
        _deploymentAccountDataProvider = deploymentAccountDataProvider;
        _genericDataProvider = genericDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _actionHandlerRegistry = actionHandlerRegistry;
        _variableContributors = variableContributors;
        _scriptWrappers = scriptWrappers;
        _deploymentDataProvider = deploymentDataProvider;
        _serverTaskDataProvider = serverTaskDataProvider;
        _calamariGithubPackageSetting = calamariGithubPackageSetting;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
        _serverTaskLogDataProvider = serverTaskLogDataProvider;
        _activityLogDataProvider = activityLogDataProvider;
    }

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        _ctx = new DeploymentTaskContext();
        _resolvedContributor = null;
        _logSequence = 0;

        Persistence.Entities.Deployments.ActivityLog taskActivityNode = null;

        try
        {
            await LoadTaskAsync(serverTaskId, ct);
            await LoadDeploymentAsync(ct);

            taskActivityNode = await _activityLogDataProvider.AddNodeAsync(
                new Persistence.Entities.Deployments.ActivityLog
                {
                    ServerTaskId = serverTaskId,
                    Name = $"Deploy {_ctx.Deployment?.Name ?? "Unknown"}",
                    NodeType = "Task",
                    Status = "Running",
                    StartedAt = DateTimeOffset.UtcNow,
                    SortOrder = 0
                }, ct: ct).ConfigureAwait(false);

            await GeneratePlanAsync(ct);
            await ResolveVariablesAsync(ct);
            await FindTargetsAsync(ct);
            ConvertSnapshotToSteps();
            PreFilterTargetsByRoles();

            foreach (var target in _ctx.Targets)
            {
                _ctx.Target = target;
                _resolvedContributor = null;
                _ctx.ActionResults = new();

                await LoadAccountAsync(ct);
                await ContributeEndpointVariablesAsync(ct);
                await ExtractCalamariAsync(ct);
                await PrepareAndExecuteStepsAsync(taskActivityNode?.Id, ct);
            }

            await RecordCompletionAsync(true, "Deployment completed successfully");

            if (taskActivityNode != null)
                await _activityLogDataProvider.UpdateNodeStatusAsync(
                    taskActivityNode.Id, "Success", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

            await _genericDataProvider.ExecuteInTransactionAsync(
                async cancellationToken =>
                {
                    await _serverTaskDataProvider.TransitionStateAsync(
                        _ctx.Task.Id, TaskState.Executing, TaskState.Success,
                        cancellationToken).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

            Log.Information("Task {TaskId} completed successfully", serverTaskId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", serverTaskId, ex.Message);

            if (taskActivityNode != null)
                await _activityLogDataProvider.UpdateNodeStatusAsync(
                    taskActivityNode.Id, "Failed", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

            await PersistTaskLogAsync(serverTaskId, "Error", ex.Message, "System", ct);

            if (_ctx.Deployment != null)
            {
                await RecordCompletionAsync(false, ex.Message);
            }

            await _genericDataProvider.ExecuteInTransactionAsync(
                async cancellationToken =>
                {
                    await _serverTaskDataProvider.TransitionStateAsync(
                        serverTaskId, TaskState.Executing, TaskState.Failed,
                        cancellationToken).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

            throw;
        }
    }
}
