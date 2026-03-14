namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

public interface IDeploymentLifecycle : IScopedDependency
{
    void Initialize(DeploymentTaskContext ctx);
    Task EmitAsync(DeploymentLifecycleEvent @event, CancellationToken ct);
}

public interface IDeploymentLifecycleHandler : IScopedDependency
{
    int Order { get; }
    void Initialize(DeploymentTaskContext ctx);
    Task HandleAsync(DeploymentLifecycleEvent @event, CancellationToken ct);
}
