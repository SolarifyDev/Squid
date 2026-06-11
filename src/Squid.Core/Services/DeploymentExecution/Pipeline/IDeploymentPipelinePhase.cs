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
    Task OnCancelledAsync(DeploymentTaskContext ctx, CancellationToken ct);
    Task OnPausedAsync(DeploymentTaskContext ctx, CancellationToken ct);
    Task OnTimedOutAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct);

    // A transient infrastructure failure (Halibut RPC drop after the library's
    // retries, or an unreachable agent) pauses the deployment for resume rather
    // than failing it terminally — the still-running script is re-attached to on
    // resume. Mirrors OnTimedOutAsync (Paused + checkpoint preserved); distinct
    // method so the audit/log reason stays honest.
    Task OnTransientPauseAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct);
}
