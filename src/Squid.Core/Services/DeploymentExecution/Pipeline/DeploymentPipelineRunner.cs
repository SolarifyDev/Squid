using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentPipelineRunner(IEnumerable<IDeploymentPipelinePhase> phases, IDeploymentLifecycle lifecycle, IDeploymentCompletionHandler completion, ITaskCancellationRegistry registry, IServerTaskDataProvider serverTaskDataProvider) : IDeploymentTaskExecutor
{
    private const int CompletionTimeoutSeconds = 30;
    private static readonly TimeSpan DefaultDeploymentTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DefaultConcurrencyMaxWait = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan DefaultConcurrencyPollInterval = TimeSpan.FromMilliseconds(3000);

    internal TimeSpan DeploymentTimeout { get; init; } = DefaultDeploymentTimeout;
    internal TimeSpan ConcurrencyMaxWait { get; init; } = DefaultConcurrencyMaxWait;
    internal TimeSpan ConcurrencyPollInterval { get; init; } = DefaultConcurrencyPollInterval;

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = serverTaskId };

        var registryCts = registry.Register(serverTaskId);
        using var timeoutCts = new CancellationTokenSource(DeploymentTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(registryCts.Token, ct, timeoutCts.Token);

        try
        {
            await WaitForConcurrencySlotAsync(serverTaskId, linkedCts.Token).ConfigureAwait(false);

            lifecycle.Initialize(ctx);

            var ordered = phases.OrderBy(p => p.Order).ToList();

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
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !registryCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var ex = new DeploymentTimeoutException(serverTaskId, DeploymentTimeout);
            await SafeCompleteAsync(ctx, () => completion.OnFailureAsync(ctx, ex, CancellationToken.None), new DeploymentTimedOutEvent(new DeploymentEventContext { Exception = ex }));
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

    private async Task WaitForConcurrencySlotAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null || string.IsNullOrEmpty(task.ConcurrencyTag)) return;

        var deadline = DateTime.UtcNow.Add(ConcurrencyMaxWait);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var hasBlocker = await serverTaskDataProvider.HasExecutingTaskWithTagAsync(task.ConcurrencyTag, serverTaskId, ct).ConfigureAwait(false);

            if (!hasBlocker) return;

            Log.Information("Task {TaskId} waiting for concurrency slot (tag: {Tag})", serverTaskId, task.ConcurrencyTag);

            await Task.Delay(ConcurrencyPollInterval, ct).ConfigureAwait(false);
        }

        Log.Warning("Task {TaskId} exceeded concurrency wait timeout ({Timeout}), proceeding anyway", serverTaskId, ConcurrencyMaxWait);
    }
}
