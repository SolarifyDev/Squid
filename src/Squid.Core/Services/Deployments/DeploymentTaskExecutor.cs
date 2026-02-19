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
    private DeploymentTaskContext _ctx;

    #region Data Providers

    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IDeploymentAccountDataProvider _deploymentAccountDataProvider;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;
    private readonly IActivityLogDataProvider _activityLogDataProvider;
    private readonly IServerTaskLogDataProvider _serverTaskLogDataProvider;

    #endregion

    #region Services

    private readonly IYamlNuGetPacker _yamlNuGetPacker;
    private readonly IDeploymentTargetFinder _targetFinder;
    private readonly IDeploymentSnapshotService _snapshotService;
    private readonly IDeploymentVariableResolver _variableResolver;
    private readonly IActionHandlerRegistry _actionHandlerRegistry;
    private readonly IEnumerable<IScriptContextWrapper> _scriptWrappers;
    private readonly IEnumerable<IEndpointVariableContributor> _variableContributors;

    #endregion

    #region Infrastructure

    private readonly HalibutRuntime _halibutRuntime;
    private readonly CalamariGithubPackageSetting _calamariGithubPackageSetting;

    public DeploymentTaskExecutor(
        IGenericDataProvider genericDataProvider, 
        IReleaseDataProvider releaseDataProvider, 
        IServerTaskDataProvider serverTaskDataProvider, 
        IDeploymentDataProvider deploymentDataProvider, 
        IDeploymentAccountDataProvider deploymentAccountDataProvider, 
        IDeploymentCompletionDataProvider deploymentCompletionDataProvider, 
        IActivityLogDataProvider activityLogDataProvider, 
        IServerTaskLogDataProvider serverTaskLogDataProvider, 
        IYamlNuGetPacker yamlNuGetPacker, 
        IDeploymentTargetFinder targetFinder, 
        IDeploymentSnapshotService snapshotService, 
        IDeploymentVariableResolver variableResolver, 
        IActionHandlerRegistry actionHandlerRegistry, 
        IEnumerable<IScriptContextWrapper> scriptWrappers, 
        IEnumerable<IEndpointVariableContributor> variableContributors, 
        HalibutRuntime halibutRuntime, 
        CalamariGithubPackageSetting calamariGithubPackageSetting)
    {
        _genericDataProvider = genericDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _serverTaskDataProvider = serverTaskDataProvider;
        _deploymentDataProvider = deploymentDataProvider;
        _deploymentAccountDataProvider = deploymentAccountDataProvider;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
        _activityLogDataProvider = activityLogDataProvider;
        _serverTaskLogDataProvider = serverTaskLogDataProvider;
        _yamlNuGetPacker = yamlNuGetPacker;
        _targetFinder = targetFinder;
        _snapshotService = snapshotService;
        _variableResolver = variableResolver;
        _actionHandlerRegistry = actionHandlerRegistry;
        _scriptWrappers = scriptWrappers;
        _variableContributors = variableContributors;
        _halibutRuntime = halibutRuntime;
        _calamariGithubPackageSetting = calamariGithubPackageSetting;
    }

    #endregion


    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        _ctx = new DeploymentTaskContext();

        try
        {
            await LoadDeploymentDataAsync(serverTaskId, ct);
            await CreateTaskActivityNodeAsync(ct);
            await PrepareAllTargetsAsync(ct);
            await DownloadCalamariAsync(ct);
            await ExtractCalamariOnAllTargetsAsync(ct);
            await ExecuteDeploymentStepsAsync(ct);
            await RecordSuccessAsync(ct);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(serverTaskId, ex, ct);
            throw;
        }
    }
}
