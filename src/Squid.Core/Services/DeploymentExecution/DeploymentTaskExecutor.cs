using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.Snapshots;

namespace Squid.Core.Services.DeploymentExecution;

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
    private readonly IServerTaskService _serverTaskService;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IEnvironmentDataProvider _environmentDataProvider;
    private readonly IDeploymentAccountDataProvider _deploymentAccountDataProvider;
    private readonly ICertificateDataProvider _certificateDataProvider;
    private readonly IDeploymentCompletionDataProvider _deploymentCompletionDataProvider;
    private readonly IReleaseSelectedPackageDataProvider _releaseSelectedPackageDataProvider;

    #endregion

    #region Services

    private readonly IYamlNuGetPacker _yamlNuGetPacker;
    private readonly IDeploymentTargetFinder _targetFinder;
    private readonly IDeploymentSnapshotService _snapshotService;
    private readonly IDeploymentVariableResolver _variableResolver;
    private readonly IActionHandlerRegistry _actionHandlerRegistry;
    private readonly ITransportRegistry _transportRegistry;
    private readonly IAutoDeployService _autoDeployService;
    private readonly IDeploymentLifecycle _lifecycle;

    #endregion

    public DeploymentTaskExecutor(
        IGenericDataProvider genericDataProvider,
        IReleaseDataProvider releaseDataProvider,
        IReleaseSelectedPackageDataProvider releaseSelectedPackageDataProvider,
        IServerTaskService serverTaskService,
        IDeploymentDataProvider deploymentDataProvider,
        IProjectDataProvider projectDataProvider,
        IEnvironmentDataProvider environmentDataProvider,
        IDeploymentAccountDataProvider deploymentAccountDataProvider,
        ICertificateDataProvider certificateDataProvider,
        IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
        IYamlNuGetPacker yamlNuGetPacker,
        IDeploymentTargetFinder targetFinder,
        IDeploymentSnapshotService snapshotService,
        IDeploymentVariableResolver variableResolver,
        IActionHandlerRegistry actionHandlerRegistry,
        ITransportRegistry transportRegistry,
        IAutoDeployService autoDeployService,
        IDeploymentLifecycle lifecycle)
    {
        _genericDataProvider = genericDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _releaseSelectedPackageDataProvider = releaseSelectedPackageDataProvider;
        _serverTaskService = serverTaskService;
        _deploymentDataProvider = deploymentDataProvider;
        _projectDataProvider = projectDataProvider;
        _environmentDataProvider = environmentDataProvider;
        _deploymentAccountDataProvider = deploymentAccountDataProvider;
        _certificateDataProvider = certificateDataProvider;
        _deploymentCompletionDataProvider = deploymentCompletionDataProvider;
        _yamlNuGetPacker = yamlNuGetPacker;
        _targetFinder = targetFinder;
        _snapshotService = snapshotService;
        _variableResolver = variableResolver;
        _actionHandlerRegistry = actionHandlerRegistry;
        _transportRegistry = transportRegistry;
        _autoDeployService = autoDeployService;
        _lifecycle = lifecycle;
    }

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        _ctx = new DeploymentTaskContext { ServerTaskId = serverTaskId };
        _lifecycle.Initialize(_ctx);

        try
        {
            await LoadTaskAsync(ct);
            await LoadDeploymentDataAsync(ct);
            await CreateTaskActivityNodeAsync(ct);
            await LogDeploymentDataSummaryAsync(ct);
            await PrepareAllTargetsAsync(ct);
            await ExecuteDeploymentStepsAsync(ct);
            await RecordSuccessAsync(ct);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(ex, ct);
            throw;
        }
    }
}
