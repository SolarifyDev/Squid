using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Jobs;

namespace Squid.Core.Services.Deployments.LifeCycle;

public interface IAutoDeployService : IScopedDependency
{
    Task TriggerAutoDeploymentsAsync(int deploymentId, CancellationToken cancellationToken);
}

public class AutoDeployService(
    ILifecycleResolver lifecycleResolver,
    ILifecycleProgressionEvaluator progressionEvaluator,
    IDeploymentDataProvider deploymentDataProvider,
    IReleaseDataProvider releaseDataProvider,
    ISquidBackgroundJobClient backgroundJobClient,
    IRepository repository) : IAutoDeployService
{
    public async Task TriggerAutoDeploymentsAsync(int deploymentId, CancellationToken cancellationToken)
    {
        var deployment = await deploymentDataProvider.GetDeploymentByIdAsync(deploymentId, cancellationToken).ConfigureAwait(false);
        if (deployment == null) return;

        var release = await releaseDataProvider.GetReleaseByIdAsync(deployment.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (release == null) return;

        var lifecycle = await lifecycleResolver.ResolveLifecycleAsync(
            release.ProjectId, release.ChannelId, cancellationToken).ConfigureAwait(false);

        var progression = await progressionEvaluator.EvaluateProgressionAsync(
            lifecycle.Id, release.ProjectId, cancellationToken).ConfigureAwait(false);

        await EnqueueAutoDeploymentsAsync(release, deployment, progression, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnqueueAutoDeploymentsAsync(
        Persistence.Entities.Deployments.Release release,
        Deployment deployment,
        PhaseProgressionResult progression,
        CancellationToken cancellationToken)
    {
        foreach (var environmentId in progression.AutoDeployEnvironmentIds)
        {
            var alreadyDeployed = await HasExistingDeploymentAsync(
                release.Id, environmentId, cancellationToken).ConfigureAwait(false);

            if (alreadyDeployed)
            {
                Log.Information(
                    "Skipping auto-deploy for release {ReleaseId} to environment {EnvironmentId}: already deployed",
                    release.Id, environmentId);
                continue;
            }

            Log.Information(
                "Enqueueing auto-deploy for release {ReleaseId} to environment {EnvironmentId}",
                release.Id, environmentId);

            backgroundJobClient.Enqueue<IDeploymentTaskExecutor>(
                executor => executor.ProcessAsync(0, CancellationToken.None),
                queue: "deployment");
        }
    }

    private async Task<bool> HasExistingDeploymentAsync(int releaseId, int environmentId, CancellationToken cancellationToken)
    {
        var existing = await repository.QueryNoTracking<Deployment>(d =>
                d.ReleaseId == releaseId && d.EnvironmentId == environmentId)
            .AnyAsync(cancellationToken).ConfigureAwait(false);

        return existing;
    }
}
