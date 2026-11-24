using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Cancel;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentPipelineRunner(IEnumerable<IDeploymentPipelinePhase> phases, IDeploymentLifecycle lifecycle, IDeploymentCompletionHandler completion, ITaskCancellationRegistry registry, IServerTaskDataProvider serverTaskDataProvider, ISquidBackgroundJobClient backgroundJobClient, IServerTaskCancelWatcher cancelWatcher = null) : IDeploymentTaskExecutor
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

    internal const string HostShutdownPauseReason = "the server is shutting down; the deployment is paused and will resume when the job is re-delivered (or can be resumed manually)";

    internal TimeSpan DeploymentTimeout { get; init; } = ResolveDeploymentTimeout();
    internal bool TimeoutResumable { get; init; } = ResolveTimeoutResumable();

    public async Task ProcessAsync(int serverTaskId, CancellationToken ct, bool resume = false)
    {
        Persistence.Entities.Deployments.ServerTask task;

        try
        {
            task = await serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(serverTaskId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The job can be interrupted on its first read, before it has claimed a lease.
            await PauseForHostShutdownAsync(new DeploymentTaskContext { ServerTaskId = serverTaskId }).ConfigureAwait(false);
            throw;
        }

        if (task?.IsTriggerTask != true)
        {
            await ExecuteAsync(serverTaskId, task, resume, ct).ConfigureAwait(false);
            return;
        }

        var paused = task.State == TaskState.Paused && (resume || task.ResumeOnRedelivery);

        await serverTaskDataProvider.RunWithTriggerTaskLockAsync(serverTaskId, waitForOwner: paused,
            () => DeliverOwnedTaskAsync(serverTaskId, resume, paused ? task.DataVersion : null, ct), ct).ConfigureAwait(false);
    }

    private async Task DeliverOwnedTaskAsync(int taskId, bool resume, byte[] pausedVersion, CancellationToken ct)
    {
        var task = await serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(taskId, ct).ConfigureAwait(false);

        if (task?.IsTriggerTask != true) return;
        if (pausedVersion != null && !pausedVersion.AsSpan().SequenceEqual(task.DataVersion)) return;
        if (task.State is not (TaskState.Pending or TaskState.Executing or TaskState.Paused or TaskState.Cancelling)) return;

        await ExecuteAsync(taskId, task, resume, ct).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        int taskId, Persistence.Entities.Deployments.ServerTask task, bool resume, CancellationToken ct)
    {
        if (task != null)
        {
            if (TaskState.IsTerminal(task.State)) return;
            if (task.State == TaskState.Paused && !resume && !task.ResumeOnRedelivery) return;
            if (await serverTaskDataProvider.IsImportedAsync(taskId, ct).ConfigureAwait(false)) return;

            if (task.IsTriggerTask && task.State == TaskState.Cancelling)
            {
                await TryCompleteUnleasedCancellationAsync(taskId, ct).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(task.ConcurrencyTag)
                && await serverTaskDataProvider.HasActiveTaskWithTagAsync(task.ConcurrencyTag, taskId, ct).ConfigureAwait(false))
            {
                await RequeueAsync(taskId, resume, task.IsTriggerTask, ct).ConfigureAwait(false);
                return;
            }
        }

        await RunDeploymentAsync(taskId, task?.IsTriggerTask == true, resume, ct).ConfigureAwait(false);
    }

    private async Task TryCompleteUnleasedCancellationAsync(int taskId, CancellationToken ct)
    {
        var context = new DeploymentTaskContext { ServerTaskId = taskId };

        if (!await serverTaskDataProvider.TryClaimUnleasedCancellationAsync(taskId, context.LeaseOwner, ct).ConfigureAwait(false)) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));
        lifecycle.Initialize(context);

        await lifecycle.EmitAsync(new DeploymentCancelledEvent(new DeploymentEventContext()), timeout.Token).ConfigureAwait(false);
        await completion.OnCancelledAsync(context, timeout.Token).ConfigureAwait(false);
    }

    private async Task RequeueAsync(int taskId, bool resume, bool requireJob, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} waiting for concurrency slot; re-enqueuing in {Delay}s", taskId, ConcurrencyRequeueDelay.TotalSeconds);

        var jobId = backgroundJobClient.Schedule<IDeploymentTaskExecutor>(
            executor => executor.ProcessAsync(taskId, CancellationToken.None, resume), ConcurrencyRequeueDelay);

        if (requireJob && string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException($"The background job client did not schedule deployment task {taskId} for its concurrency slot.");

        if (!string.IsNullOrEmpty(jobId))
            await serverTaskDataProvider.SetJobIdAsync(taskId, jobId, ct).ConfigureAwait(false);
    }

    private async Task RunDeploymentAsync(int serverTaskId, bool isTriggerTask, bool resume, CancellationToken ct)
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = serverTaskId };

        var registryCts = registry.Register(serverTaskId);
        using var timeoutCts = new CancellationTokenSource(DeploymentTimeout);
        using var leaseLostCts = new CancellationTokenSource();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(registryCts.Token, ct, timeoutCts.Token, leaseLostCts.Token);

        ctx.ShouldCancelRemoteScript = BuildRemoteScriptCancelDecision(ctx, registryCts, timeoutCts, leaseLostCts, ct);

        IAsyncDisposable cancelWatch = null;
        IDisposable leaseLostLink = null;

        try
        {
            lifecycle.Initialize(ctx);

            var ordered = phases.OrderBy(p => p.Order).ToList();

            foreach (var phase in ordered)
            {
                await phase.ExecuteAsync(ctx, linkedCts.Token);

                PromoteOnceClaimed(ctx, registryCts);

                cancelWatch ??= StartCancelWatchOnceClaimed(ctx);

                leaseLostLink ??= StopWhenLeaseLost(ctx, leaseLostCts);
            }

            if (IsLeaseLost(ctx)) return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CompletionTimeoutSeconds));
            await lifecycle.EmitAsync(new PackagesReleasedEvent(new DeploymentEventContext()), timeout.Token);

            // A user cancel can reach the database before the running pod's watcher sees it.
            // Once every phase has finished, host shutdown alone leaves the final outcome intact.
            var userCancelled = registryCts.IsCancellationRequested
                || await serverTaskDataProvider.IsCancellationRequestedAsync(serverTaskId, timeout.Token).ConfigureAwait(false);

            if (IsLeaseLost(ctx)) return;

            if (userCancelled || registryCts.IsCancellationRequested)
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
        catch (DeploymentSuspendedException suspended)
        {
            // Routed through SafeCompleteAsync like every other terminal-handling catch path: OnPausedAsync
            // now writes state, so it can fail (a racing cancel, a DB blip), and an escaping
            // exception here would surface as a hard job failure that contradicts the pause the
            // pipeline already decided on. It was safe to call directly only while it just logged.
            await SafeCompleteAsync(
                ctx,
                () => completion.OnPausedAsync(ctx, CancellationToken.None),
                new DeploymentPausedEvent(new DeploymentEventContext { PauseReason = suspended.OperatorReason }));
        }
        catch (OperationCanceledException) when (HasLostLease(ctx))
        {
            // The lease was taken from this run (the reaper recovered the task, another run took it over, or this run could not renew for so long that it
            // must assume so): the lease-lost signal stopped the pipeline. The task belongs to someone else now, so nothing is written — not the state, the
            // checkpoint or a completion record — and the agent scripts are left running for whoever resumes it (see BuildRemoteScriptCancelDecision).
            Log.Warning("[Deploy] Task {TaskId} was stopped because this run lost its lease; leaving the task and its agent scripts to the new owner", serverTaskId);
        }
        catch (OperationCanceledException) when (IsHostShutdown(registryCts, ct))
        {
            // The job host is stopping (Hangfire substitutes its shutdown-linked token for `ct`) and the
            // user did NOT cancel. The task is still Executing, so the Cancelling → Cancelled write of the
            // cancel path could never apply: it would throw, SafeCompleteAsync would swallow it, the job
            // would return normally (Hangfire marks it Succeeded, never re-delivers) and the task would
            // wedge in Executing holding its environment's concurrency slot. Pause instead — checkpoint and
            // in-flight script pointers preserved (the observer left the script running, see
            // BuildRemoteScriptCancelDecision) — then rethrow: Hangfire treats an OperationCanceledException
            // raised while its shutdown token is cancelled as an interrupted job and re-queues it rather
            // than failing it, so the restarted server resumes from the checkpoint.
            if (await IsUserCancelPersistedAsync(serverTaskId).ConfigureAwait(false))
            {
                Log.Information("[Deploy] Task {TaskId} was aborted by its job token but the user had cancelled it; completing the cancel", serverTaskId);

                await SafeCompleteAsync(ctx, () => completion.OnCancelledAsync(ctx, CancellationToken.None), new DeploymentCancelledEvent(new DeploymentEventContext()));

                return;
            }

            await PauseForHostShutdownAsync(ctx).ConfigureAwait(false);

            throw;
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
        catch (ServerTaskOwnedByLiveProcessException ex)
        {
            // Cancellation may have arrived between the trigger's locked read and the claim.
            // A live or stale lease still belongs to its owner or the reaper.
            if (isTriggerTask && ex.LeaseOwner == null)
                await TryCompleteUnleasedCancellationAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            else
                Log.Warning("[Deploy] Task {TaskId} is already owned by {Owner}; duplicate delivery suppressed", ex.ServerTaskId, ex.LeaseOwner);
        }
        catch (ServerTaskStateTransitionException ex) when (ctx.Task == null && ex.ToState == TaskState.Executing && TaskState.IsTerminal(ex.FromState))
        {
            // An operator completed cancellation before LoadTaskPhase claimed the task.
            return;
        }
        catch (ServerTaskStateTransitionException ex) when (ctx.Task == null && ex.ToState == TaskState.Executing && ex.FromState == TaskState.Cancelling)
        {
            if (isTriggerTask)
                await TryCompleteUnleasedCancellationAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ConcurrencySlotOccupiedException ex)
        {
            Log.Information("[Deploy] Task {TaskId} lost concurrency slot at claim (tag: {Tag}); re-enqueuing", serverTaskId, ex.ConcurrencyTag);

            await RequeueAsync(serverTaskId, resume, isTriggerTask, CancellationToken.None).ConfigureAwait(false);
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
            leaseLostLink?.Dispose();

            if (cancelWatch != null) await cancelWatch.DisposeAsync().ConfigureAwait(false);

            registry.Unregister(serverTaskId, registryCts);

            if (ctx.Lease != null) await ctx.Lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task PauseForHostShutdownAsync(DeploymentTaskContext ctx)
    {
        Log.Information("[Deploy] Task {TaskId} interrupted by host shutdown; pausing for resume on job re-delivery", ctx.ServerTaskId);

        ctx.ResumeOnRedelivery = true;

        return SafeCompleteAsync(ctx, () => completion.OnPausedAsync(ctx, CancellationToken.None),
            new DeploymentPausedEvent(new DeploymentEventContext { PauseReason = HostShutdownPauseReason }));
    }

    /// <summary>How long the shutdown-vs-cancel row read may take; the read is async and off any cancellation callback.</summary>
    internal static readonly TimeSpan ExternalCancelProbeTimeout = TimeSpan.FromSeconds(5);

    // The external (Hangfire) token fired. It means one of two things: the host is shutting down, or the user cancelled a task running on a pod
    // that is not the one that served the cancel (Hangfire aborts the deleted job's token there). Only the row can say which: Cancelling is a user
    // cancel. Read here, in the async catch, never on the cancellation callback thread. Unreadable stays a shutdown (the established behaviour).
    private async Task<bool> IsUserCancelPersistedAsync(int serverTaskId)
    {
        try
        {
            using var probeCts = new CancellationTokenSource(ExternalCancelProbeTimeout);

            var task = await serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(serverTaskId, probeCts.Token).ConfigureAwait(false);

            return task != null && string.Equals(task.State, TaskState.Cancelling, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Could not read task {TaskId} to tell a user cancel from a host shutdown; treating it as a shutdown", serverTaskId);

            return false;
        }
    }

    // This run only becomes the task's registered cancel target once it holds the lease: a duplicate delivery that the claim fences out must
    // never displace (and so never disable user-cancel or the cancel watcher for) the live run that owns the task. Idempotent.
    private void PromoteOnceClaimed(DeploymentTaskContext ctx, CancellationTokenSource registryCts)
    {
        if (ctx.Lease == null) return;

        registry.Promote(ctx.ServerTaskId, registryCts);
    }

    // Only a pipeline that holds the task's lease (the claim fenced out any live owner) watches for a cross-pod cancel: a duplicate delivery of a
    // task another pod is running must never be cancelled — and so complete the task — on that pod's behalf. The signal is the same registry cancel
    // a same-pod cancel uses, so user-cancel precedence, the remote CancelScript and Cancelling → Cancelled are identical for both.
    private IAsyncDisposable StartCancelWatchOnceClaimed(DeploymentTaskContext ctx)
    {
        if (cancelWatcher == null || ctx.Lease == null) return null;

        return cancelWatcher.Start(ctx.ServerTaskId, () => registry.TryCancel(ctx.ServerTaskId));
    }

    // The run only watches for losing the lease once it holds one (after the claim); a lease that was already lost by then stops the run at once.
    private static IDisposable StopWhenLeaseLost(DeploymentTaskContext ctx, CancellationTokenSource leaseLostCts)
    {
        if (ctx.Lease == null) return null;

        return ctx.Lease.LostToken.Register(leaseLostCts.Cancel);
    }

    private static bool HasLostLease(DeploymentTaskContext ctx) => ctx.Lease is { IsLost: true };

    /// <summary>
    /// Whether an in-flight remote script must be cancelled on the agent when the run is interrupted:
    /// on every interruption EXCEPT a recognised pause, so any cancellation source not listed here
    /// (e.g. a required step's fail-fast peer cancellation, whose task then Fails) keeps the legacy
    /// cancel behaviour instead of silently orphaning the script. A pause — the task keeps its in-flight
    /// pointer for resume — is: not user-cancelled AND (a resumable deployment timeout fired OR the
    /// external job token (host shutdown) fired OR a step suspended the task for an interruption), or a
    /// lost lease (whoever owns the task now decides what happens to its scripts, even if the user also cancelled). Reads the source flags (set before any linked token's
    /// callbacks run), never a linked token, so the observer cannot see its own cancellation before the
    /// decision flips; <paramref name="externalCt"/> is a source token, not linked, so it is race-free too.
    /// </summary>
    private RemoteScriptCancelDecision BuildRemoteScriptCancelDecision(DeploymentTaskContext ctx, CancellationTokenSource registryCts, CancellationTokenSource timeoutCts, CancellationTokenSource leaseLostCts, CancellationToken externalCt)
    {
        bool IsPausing() => leaseLostCts.IsCancellationRequested || (!registryCts.IsCancellationRequested && ((TimeoutResumable && timeoutCts.IsCancellationRequested) || externalCt.IsCancellationRequested || ctx.Suspended != null));

        return () => !IsPausing();
    }

    /// <summary>
    /// The external job token fired and the user did not cancel: the job host is shutting down. A user
    /// cancel always wins when both are signalled (the task is then Cancelling and ends Cancelled).
    /// </summary>
    private static bool IsHostShutdown(CancellationTokenSource registryCts, CancellationToken externalCt)
        => externalCt.IsCancellationRequested && !registryCts.IsCancellationRequested;

    // The reaper already recovered this task (paused it, or completed its cancel): whatever this process was about to record would overwrite that
    // decision (a Paused task flipped to Failed, its checkpoint deleted), so the outcome is left as the reaper wrote it.
    private static bool IsLeaseLost(DeploymentTaskContext ctx)
    {
        if (ctx.Lease is not { IsLost: true }) return false;

        Log.Warning("[Deploy] Task {TaskId} lost its lease while running (recovered by the reaper or taken over); not recording an outcome, leaving the task state as recovered", ctx.ServerTaskId);

        return true;
    }

    private async Task SafeCompleteAsync(DeploymentTaskContext ctx, Func<Task> completionAction, DeploymentLifecycleEvent lifecycleEvent)
    {
        if (IsLeaseLost(ctx)) return;

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