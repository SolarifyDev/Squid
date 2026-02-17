using Halibut;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
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

    private DeploymentTaskContext _ctx;
    private IEndpointVariableContributor _resolvedContributor;

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
    }

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        _ctx = new DeploymentTaskContext();
        _resolvedContributor = null;

        try
        {
            await LoadTaskAsync(serverTaskId, ct);
            await LoadDeploymentAsync(ct);
            await GeneratePlanAsync(ct);
            await ResolveVariablesAsync(ct);
            await FindTargetsAsync(ct);
            ConvertSnapshotToSteps();

            foreach (var target in _ctx.Targets)
            {
                _ctx.Target = target;
                _resolvedContributor = null;
                _ctx.ActionResults = new();

                await LoadAccountAsync(ct);
                await ContributeEndpointVariablesAsync(ct);
                await ExtractCalamariAsync(ct);
                await PrepareAndExecuteStepsAsync(ct);
            }

            await RecordCompletionAsync(true, "Deployment completed successfully");

            await _genericDataProvider.ExecuteInTransactionAsync(
                async cancellationToken =>
                {
                    await _serverTaskDataProvider.UpdateServerTaskStateAsync(
                        _ctx.Task.Id, "Success", cancellationToken: cancellationToken).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

            Log.Information("Task {TaskId} completed successfully", serverTaskId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", serverTaskId, ex.Message);

            if (_ctx.Deployment != null)
            {
                await RecordCompletionAsync(false, ex.Message);
            }

            await _genericDataProvider.ExecuteInTransactionAsync(
                async cancellationToken =>
                {
                    await _serverTaskDataProvider.UpdateServerTaskStateAsync(
                        serverTaskId, "Failed", cancellationToken: cancellationToken).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

            throw;
        }
    }
}
