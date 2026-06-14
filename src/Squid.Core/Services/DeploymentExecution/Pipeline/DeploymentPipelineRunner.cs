using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentPipelineRunner(IEnumerable<IDeploymentPipelinePhase> phases, IDeploymentLifecycle lifecycle, IDeploymentCompletionHandler completion, ITaskCancellationRegistry registry, IServerTaskDataProvider serverTaskDataProvider, ISquidBackgroundJobClient backgroundJobClient) : IDeploymentTaskExecutor
{
    private const int CompletionTimeoutSeconds = 30;

    /// <summary>
    /// How long to defer a re-enqueued deployment that could not get its environment
    /// concurrency slot. Cross-process serialization is now DB-enforced (the
    /// <c>ux_server_task_active_per_tag</c> unique index), so a blocked deployment is not
    /// run-anyway and is not held in-process — it stays Pending and is re-dispatched after this
    /// delay (by any pod) until the slot frees. Sibling of the per-machine poll cadence; kept a
    /// plain const matching the prior concurrency-poll settings (no env var).
    /// </summary>
    internal static readonly TimeSpan ConcurrencyRequeueDelay = TimeSpan.FromSeconds(15);

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

    /// <summary>
    /// Operator escape hatch (Rule 8): controls what happens when a deployment
    /// exceeds <see cref="DeploymentTimeoutMinutesEnvVar"/>. The safe default
    /// (unset / blank / unrecognised) is <c>true</c> — the task is paused and
    /// its checkpoint preserved so it can be resumed (POST tasks/{id}/resume)
    /// instead of failing irrecoverably. Set this env var to a falsey value
    /// (<c>false</c>/<c>0</c>/<c>no</c>/<c>off</c>, case-insensitive) to restore
    /// the historical fail-fast behaviour: a timed-out deployment transitions to
    /// Failed and its checkpoint is deleted. Operators who key alerting off the
    /// terminal Failed state (rather than the resumable Paused state) can opt out
    /// here without a code change.
    /// </summary>
    public const string DeploymentTimeoutResumableEnvVar = "SQUID_DEPLOYMENT_TIMEOUT_RESUMABLE";

    internal const bool DefaultDeploymentTimeoutResumable = true;

    internal TimeSpan DeploymentTimeout { get; init; } = ResolveDeploymentTimeout();
    internal bool TimeoutResumable { get; init; } = ResolveTimeoutResumable();

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = serverTaskId };

        var registryCts = registry.Register(serverTaskId);
        using var timeoutCts = new CancellationTokenSource(DeploymentTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(registryCts.Token, ct, timeoutCts.Token);

        try
        {
            var slot = await EvaluateSlotAsync(serverTaskId, linkedCts.Token).ConfigureAwait(false);

            if (slot == SlotDecision.AlreadyResolved)
            {
                Log.Information("[Deploy] Task {TaskId} is already in a terminal state; skipping re-dispatched run", serverTaskId);
                return;
            }

            if (slot == SlotDecision.SlotBusy)
            {
                await RequeueForConcurrencySlotAsync(serverTaskId, linkedCts.Token).ConfigureAwait(false);
                return;
            }

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

            Func<Task> onTimeout = TimeoutResumable
                ? () => completion.OnTimedOutAsync(ctx, ex, CancellationToken.None)
                : () => completion.OnFailureAsync(ctx, ex, CancellationToken.None);

            await SafeCompleteAsync(ctx, onTimeout, new DeploymentTimedOutEvent(new DeploymentEventContext { Exception = ex }));
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            await SafeCompleteAsync(ctx, () => completion.OnCancelledAsync(ctx, CancellationToken.None), new DeploymentCancelledEvent(new DeploymentEventContext()));
        }
        catch (ConcurrencySlotOccupiedException ex)
        {
            // Lost the environment slot in the TOCTOU window between the free-slot check above
            // and LoadTaskPhase's atomic →Executing claim — a peer pod's active task held it and
            // the ux_server_task_active_per_tag unique index rejected this claim. Re-enqueue (the
            // task stays Pending, or Paused if this was a resume) instead of failing: never
            // overlapped, never lost. No completion record is written — the deployment has not
            // started.
            Log.Information("[Deploy] Task {TaskId} lost concurrency slot at claim (tag: {Tag}); re-enqueuing", serverTaskId, ex.ConcurrencyTag);
            await RequeueForConcurrencySlotAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (!timeoutCts.IsCancellationRequested && !registryCts.IsCancellationRequested && !ct.IsCancellationRequested
            && TargetCatchClassifier.IsTransientInfraFailure(ex))
        {
            // A transient infra failure (Halibut RPC drop after the library's
            // retries, or an unreachable agent) pauses the
            // deployment instead of failing it: the in-flight script pointer is
            // preserved (the strategy clears it only on a definitive observation),
            // so a resume re-attaches to the still-running script rather than
            // re-dispatching a duplicate. We do NOT rethrow — Paused is a clean,
            // expected outcome. This is unconditional (no opt-out): failing fast on
            // a transient blip would just discard already-completed progress and
            // risk a duplicate run, which has no legitimate use case.
            //
            // The cancel/timeout guard preserves the established precedence: a
            // user-cancel (registryCts/ct) or a wall-clock timeout (timeoutCts)
            // that races a transient RPC drop must NOT be reclassified as a
            // transient pause — cancel/timeout win, so the exception falls through
            // to their handlers (or the generic failure path).
            await SafeCompleteAsync(ctx, () => completion.OnTransientPauseAsync(ctx, ex, CancellationToken.None), new DeploymentPausedEvent(new DeploymentEventContext()));
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

    private enum SlotDecision { Proceed, SlotBusy, AlreadyResolved }

    // One read decides the cold-path outcome: (a) AlreadyResolved if the task is terminal —
    // covers a re-dispatched job whose task was cancelled while it sat re-enqueued, so it
    // short-circuits instead of trying an illegal Cancelled→Executing claim; (b) SlotBusy if
    // another ACTIVE task (Executing/Paused/Cancelling) holds the same tag's slot; (c) Proceed
    // otherwise. A false negative on (b) under a race is harmless — LoadTaskPhase's atomic claim
    // (the unique index) still rejects an overlapping →Executing and re-enqueues. An untagged
    // task is never slot-blocked.
    private async Task<SlotDecision> EvaluateSlotAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null) return SlotDecision.Proceed;

        if (TaskState.IsTerminal(task.State)) return SlotDecision.AlreadyResolved;

        if (string.IsNullOrEmpty(task.ConcurrencyTag)) return SlotDecision.Proceed;

        return await serverTaskDataProvider.HasActiveTaskWithTagAsync(task.ConcurrencyTag, serverTaskId, ct).ConfigureAwait(false)
            ? SlotDecision.SlotBusy
            : SlotDecision.Proceed;
    }

    // Re-dispatch the still-Pending (or Paused, on resume) task after a short delay so any pod
    // can retry the slot once the occupant finishes. Replaces the legacy in-process "wait up to
    // 300s then proceed anyway" poll: the worker is freed immediately and the deployment is never
    // run while the slot is held. The new job id is persisted to task.JobId so a subsequent
    // cancel targets THIS scheduled job rather than the already-finished original.
    private async Task RequeueForConcurrencySlotAsync(int serverTaskId, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} waiting for concurrency slot; re-enqueuing in {Delay}s", serverTaskId, ConcurrencyRequeueDelay.TotalSeconds);

        var jobId = backgroundJobClient.Schedule<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTaskId, CancellationToken.None), ConcurrencyRequeueDelay);

        if (!string.IsNullOrEmpty(jobId))
            await serverTaskDataProvider.SetJobIdAsync(serverTaskId, jobId, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Parses the operator-supplied resumable-timeout flag. The safe default is
    /// <c>true</c> (pause + preserve checkpoint on timeout) — only an explicit
    /// falsey token (<c>false</c>/<c>0</c>/<c>no</c>/<c>off</c>, case-insensitive,
    /// surrounding whitespace tolerated) opts back into fail-fast. Anything
    /// unrecognised falls back to the safe resumable default so a typo can never
    /// silently throw away a deployment's progress. Pure + static (surfaced to
    /// the unit suite via InternalsVisibleTo) so the full input matrix is
    /// testable without the pipeline.
    /// </summary>
    internal static bool ParseTimeoutResumable(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultDeploymentTimeoutResumable;

        var value = raw.Trim();

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0"
            || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;

        return DefaultDeploymentTimeoutResumable;
    }

    private static bool ResolveTimeoutResumable()
        => ParseTimeoutResumable(Environment.GetEnvironmentVariable(DeploymentTimeoutResumableEnvVar));
}
