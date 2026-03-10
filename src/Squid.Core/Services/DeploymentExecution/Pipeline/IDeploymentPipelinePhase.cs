namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public interface IDeploymentPipelinePhase : IScopedDependency
{
    int Order { get; }
    Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct);
}

public interface IDeploymentCompletionHandler : IScopedDependency
{
    Task OnSuccessAsync(DeploymentTaskContext ctx, CancellationToken ct);
    Task OnFailureAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct);
}
