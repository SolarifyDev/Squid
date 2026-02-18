using Halibut;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.Snapshots;
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
    private readonly IDeploymentSnapshotService _snapshotService;
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
        IDeploymentSnapshotService snapshotService,
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
        _snapshotService = snapshotService;
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

        try
        {
            await LoadDeploymentDataAsync(serverTaskId, ct);
            await CreateTaskActivityNodeAsync(ct);

            foreach (var target in _ctx.Targets)
            {
                _ctx.Target = target;
                _resolvedContributor = null;
                _ctx.ActionResults = new();

                await LoadTargetDataAsync(ct);
                await ExtractCalamariAsync(ct);
                await PrepareAndExecuteStepsAsync(ct);
            }

            await RecordSuccessAsync(ct);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(serverTaskId, ex, ct);
            throw;
        }
    }
}
