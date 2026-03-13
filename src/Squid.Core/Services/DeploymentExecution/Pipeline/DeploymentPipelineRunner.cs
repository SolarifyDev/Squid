using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;

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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));
            await lifecycle.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), timeout.Token);
            await completion.OnSuccessAsync(ctx, timeout.Token);
        }
        catch (DeploymentSuspendedException)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));
            await lifecycle.EmitAsync(new DeploymentPausedEvent(new DeploymentEventContext()), timeout.Token);
            await completion.OnPausedAsync(ctx, timeout.Token);
        }
        catch (OperationCanceledException) when (registryCts.IsCancellationRequested)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));
            await lifecycle.EmitAsync(new DeploymentCancelledEvent(new DeploymentEventContext()), timeout.Token);
            await completion.OnCancelledAsync(ctx, timeout.Token);
        }
        catch (Exception ex)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));
            await lifecycle.EmitAsync(new DeploymentFailedEvent(new DeploymentEventContext { Exception = ex }), timeout.Token);
            await completion.OnFailureAsync(ctx, ex, timeout.Token);
            throw;
        }
        finally
        {
            registry.Unregister(serverTaskId);
        }
    }
}
