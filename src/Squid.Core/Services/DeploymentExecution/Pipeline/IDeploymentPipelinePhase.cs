using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public interface IDeploymentPipelinePhase : IScopedDependency
{
    int Order { get; }
    Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct);
}

// Every callback returns the outcome it actually left the task in, which the runner announces: a callback whose own write was refused because the user's
// cancel landed first completes that cancel and returns Cancelled, so the announcement never contradicts the persisted state.
public interface IDeploymentCompletionHandler : IScopedDependency
{
    Task<DeploymentOutcome> OnSuccessAsync(DeploymentTaskContext ctx, CancellationToken ct);
    Task<DeploymentOutcome> OnFailureAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct);
    Task<DeploymentOutcome> OnCancelledAsync(DeploymentTaskContext ctx, CancellationToken ct);
    Task<DeploymentOutcome> OnPausedAsync(DeploymentTaskContext ctx, CancellationToken ct);
    Task<DeploymentOutcome> OnTimedOutAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct);

    // A transient infrastructure failure (Halibut RPC drop after the library's
    // retries, or an unreachable agent) pauses the deployment for resume rather
    // than failing it terminally — the still-running script is re-attached to on
    // resume. Mirrors OnTimedOutAsync (Paused + checkpoint preserved); distinct
    // method so the audit/log reason stays honest.
    Task<DeploymentOutcome> OnTransientPauseAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct);
}
