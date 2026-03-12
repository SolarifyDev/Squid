using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentPipelineRunner(IEnumerable<IDeploymentPipelinePhase> phases, IDeploymentLifecycle lifecycle, IDeploymentCompletionHandler completion, ITaskCancellationRegistry registry) : IDeploymentTaskExecutor
{
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

            await lifecycle.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), CancellationToken.None);
            await completion.OnSuccessAsync(ctx, CancellationToken.None);
        }
        catch (DeploymentSuspendedException)
        {
            await lifecycle.EmitAsync(new DeploymentPausedEvent(new DeploymentEventContext()), CancellationToken.None);
            await completion.OnPausedAsync(ctx, CancellationToken.None);
        }
        catch (OperationCanceledException) when (registryCts.IsCancellationRequested)
        {
            await lifecycle.EmitAsync(new DeploymentCancelledEvent(new DeploymentEventContext()), CancellationToken.None);
            await completion.OnCancelledAsync(ctx, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await lifecycle.EmitAsync(new DeploymentFailedEvent(new DeploymentEventContext { Exception = ex }), CancellationToken.None);
            await completion.OnFailureAsync(ctx, ex, CancellationToken.None);
            throw;
        }
        finally
        {
            registry.Unregister(serverTaskId);
        }
    }
}
