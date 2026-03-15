using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentPipelineRunner(IEnumerable<IDeploymentPipelinePhase> phases, IDeploymentLifecycle lifecycle, IDeploymentCompletionHandler completion, ITaskCancellationRegistry registry) : IDeploymentTaskExecutor
{
    private const int CompletionTimeoutSeconds = 30;

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = serverTaskId };
        var registryCts = registry.Register(serverTaskId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(registryCts.Token, ct);

        lifecycle.Initialize(ctx);

        var ordered = phases.OrderBy(p => p.Order).ToList();

        try
        {
            foreach (var phase in ordered)
                await phase.ExecuteAsync(ctx, linkedCts.Token);

            await lifecycle.EmitAsync(new PackagesReleasedEvent(new DeploymentEventContext()), linkedCts.Token);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));

            if (ctx.FailureEncountered)
            {
                var ex = new InvalidOperationException("One or more steps failed during deployment");
                await lifecycle.EmitAsync(new DeploymentFailedEvent(new DeploymentEventContext { Exception = ex }), timeout.Token);
                await completion.OnFailureAsync(ctx, ex, timeout.Token);
            }
            else
            {
                await lifecycle.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), timeout.Token);
                await completion.OnSuccessAsync(ctx, timeout.Token);
            }
        }
        catch (DeploymentSuspendedException)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));
            await lifecycle.EmitAsync(new DeploymentPausedEvent(new DeploymentEventContext()), timeout.Token);
            await completion.OnPausedAsync(ctx, timeout.Token);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            await SafeCompleteAsync(ctx, () => completion.OnCancelledAsync(ctx, CancellationToken.None), new DeploymentCancelledEvent(new DeploymentEventContext()));
        }
        catch (Exception ex)
        {
            await SafeCompleteAsync(ctx, () => completion.OnFailureAsync(ctx, ex, CancellationToken.None), new DeploymentFailedEvent(new DeploymentEventContext { Exception = ex }));
            throw;
        }
        finally
        {
            registry.Unregister(serverTaskId);
        }
    }

    private async Task SafeCompleteAsync(DeploymentTaskContext ctx, Func<Task> completionAction, DeploymentLifecycleEvent lifecycleEvent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));

        try
        {
            await lifecycle.EmitAsync(lifecycleEvent, timeout.Token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to emit lifecycle event for task {TaskId}", ctx.ServerTaskId);
        }

        try
        {
            await completionAction();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to complete task {TaskId} state transition", ctx.ServerTaskId);
        }
    }
}
