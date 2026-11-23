namespace Squid.Core.Services.DeploymentExecution.Runbooks;

/// <summary>
/// Background entry point for Runbook tasks. It is deliberately separate from
/// <see cref="IDeploymentTaskExecutor"/> so a Runbook task cannot enter the
/// Release/Deployment pipeline.
/// </summary>
public interface IRunbookTaskExecutor : IScopedDependency
{
    Task ProcessAsync(int serverTaskId, CancellationToken cancellationToken);
}
