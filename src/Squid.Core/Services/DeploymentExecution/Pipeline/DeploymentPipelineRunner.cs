using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentPipelineRunner(IEnumerable<IDeploymentPipelinePhase> phases, IDeploymentLifecycle lifecycle, IDeploymentCompletionHandler completion, ITaskCancellationRegistry registry, IServerTaskDataProvider serverTaskDataProvider) : IDeploymentTaskExecutor
{
    private const int CompletionTimeoutSeconds = 30;
    private static readonly TimeSpan DefaultConcurrencyMaxWait = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan DefaultConcurrencyPollInterval = TimeSpan.FromMilliseconds(3000);

    /// <summary>
    /// Operator escape hatch (Rule 8): maximum wall-clock minutes a single
    /// deployment may run before the pipeline is force-cancelled and the task
    /// fails with <see cref="DeploymentTimeoutException"/>. Unset / blank /
    /// non-positive-integer → <see cref="DefaultDeploymentTimeoutMinutes"/>.
    /// Raise it for long-running deployments (large DB migrations, multi-stage
    /// rollouts) that legitimately exceed the default; leaving it unset
    /// preserves the historical 60-minute behaviour exactly.
    /// </summary>
    public const string DeploymentTimeoutMinutesEnvVar = "SQUID_DEPLOYMENT_TIMEOUT_MINUTES";

    internal const int DefaultDeploymentTimeoutMinutes = 60;
    private static readonly TimeSpan DefaultDeploymentTimeout = TimeSpan.FromMinutes(DefaultDeploymentTimeoutMinutes);

    internal TimeSpan DeploymentTimeout { get; init; } = ResolveDeploymentTimeout();
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

            // P0-A.1 (2026-04-24 audit): cancel wins over fail in the pre-terminal-write
            // race. If the user clicked cancel (registryCts) or the caller cancelled the
            // external token (ct) while a step failure was being captured on the context,
            // the task must end Cancelled — not Failed. Pre-fix the FailureEncountered
            // check ran first and races produced DeploymentFailedEvent while the UI
            // showed "cancel requested". Checkpoint + downstream consumers then latched
            // on the wrong terminal state. We still let timeouts fall through to the
            // OCE handler below (timeoutCts raises OCE mid-execution, not here).
            if (registryCts.Token.IsCancellationRequested || ct.IsCancellationRequested)
            {
                await lifecycle.EmitAsync(new DeploymentCancelledEvent(new DeploymentEventContext()), timeout.Token);
                await completion.OnCancelledAsync(ctx, timeout.Token);
            }
            else if (ctx.FailureEncountered)
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
            Log.Warning(ex, "[Deploy] Failed to emit lifecycle event for task {TaskId}", ctx.ServerTaskId);
        }

        try
        {
            await completionAction();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Deploy] Failed to complete task {TaskId} state transition", ctx.ServerTaskId);
        }
    }

    private async Task WaitForConcurrencySlotAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null || string.IsNullOrEmpty(task.ConcurrencyTag)) return;

        var deadline = DateTime.UtcNow.Add(ConcurrencyMaxWait);
        var logged = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var hasBlocker = await serverTaskDataProvider.HasExecutingTaskWithTagAsync(task.ConcurrencyTag, serverTaskId, ct).ConfigureAwait(false);

            if (!hasBlocker) return;

            if (!logged)
            {
                Log.Information("[Deploy] Task {TaskId} waiting for concurrency slot (tag: {Tag})", serverTaskId, task.ConcurrencyTag);
                logged = true;
            }

            await Task.Delay(ConcurrencyPollInterval, ct).ConfigureAwait(false);
        }

        Log.Warning("[Deploy] Task {TaskId} exceeded concurrency wait timeout ({Timeout}), proceeding anyway", serverTaskId, ConcurrencyMaxWait);
    }

    /// <summary>
    /// Parses the operator-supplied deployment timeout (in minutes). Blank,
    /// non-integer, or non-positive input falls back to the historical
    /// <see cref="DefaultDeploymentTimeoutMinutes"/> default so a typo'd env var
    /// can never disable the safety timer or crash construction. Pure + static
    /// (internal, surfaced to the unit suite via InternalsVisibleTo) so the full
    /// input matrix is testable without the pipeline.
    /// </summary>
    internal static TimeSpan ParseDeploymentTimeout(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultDeploymentTimeout;

        if (!int.TryParse(raw.Trim(), out var minutes) || minutes <= 0)
        {
            Log.Warning(
                "{EnvVar}='{RawValue}' is not a valid positive integer (minutes); falling back to default {Default} min.",
                DeploymentTimeoutMinutesEnvVar, raw, DefaultDeploymentTimeoutMinutes);
            return DefaultDeploymentTimeout;
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static TimeSpan ResolveDeploymentTimeout()
        => ParseDeploymentTimeout(Environment.GetEnvironmentVariable(DeploymentTimeoutMinutesEnvVar));
}
