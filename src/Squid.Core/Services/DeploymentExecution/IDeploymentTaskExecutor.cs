namespace Squid.Core.Services.DeploymentExecution;

public interface IDeploymentTaskExecutor : IScopedDependency
{
    Task ProcessAsync(int serverTaskId, CancellationToken ct);
}
