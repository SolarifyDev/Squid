namespace Squid.Core.Services.DeploymentExecution;

public interface IDeploymentTaskExecutor : IScopedDependency
{
    [Hangfire.AutomaticRetry(Attempts = 3)]
    Task ProcessAsync(int serverTaskId, CancellationToken ct, bool resume = false);
}