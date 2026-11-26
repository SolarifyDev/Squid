using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentCompletionHandler(
    IGenericDataProvider genericDataProvider,
    IServerTaskService serverTaskService,
    IDeploymentDataProvider deploymentDataProvider,
    IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
    IAutoDeployService autoDeployService,
    IDeploymentCheckpointService checkpointService,
    IInFlightScriptCanceller inFlightScriptCanceller = null,
    ServerTaskSetting serverTaskSetting = null) : IDeploymentCompletionHandler
{
    private readonly ServerTaskTerminalWriteOptions _terminalWrite = ServerTaskTerminalWriteOptions.From(serverTaskSetting);

    public async Task<DeploymentOutcome> OnSuccessAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var outcome = await TransitionOrYieldToCancelAsync(ctx, TaskState.Executing, TaskState.Success, true, ct).ConfigureAwait(false);

        if (outcome != DeploymentOutcome.Succeeded) return outcome;

        await CleanupCheckpointAsync(ctx, ct).ConfigureAwait(false);
        await TriggerAutoDeploymentsAsync(ctx, ct).ConfigureAwait(false);

        Log.Information("[Deploy] Task {TaskId} completed successfully", ctx.ServerTaskId);

        return outcome;
    }

    public async Task<DeploymentOutcome> OnFailureAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Error(ex, "[Deploy] Task {TaskId} failed", ctx.ServerTaskId);

        var fromState = await ResolveCurrentActiveStateAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        // The user's cancel wins over a failure that raced it (a failed batch or a non-resumable timeout reached before the cancel reached this run's token):
        // writing Cancelling -> Failed would end the cancel Failed and leave the agent scripts running. Complete the cancel the way a cancelled run does.
        if (IsCancelling(fromState)) return await CompleteRacingCancelAsync(ctx, TaskState.Failed, ct).ConfigureAwait(false);

        var outcome = await TransitionOrYieldToCancelAsync(ctx, fromState, TaskState.Failed, false, ct).ConfigureAwait(false);

        if (outcome != DeploymentOutcome.Failed) return outcome;

        await CleanupCheckpointAsync(ctx, ct).ConfigureAwait(false);

        return outcome;
    }

    public async Task<DeploymentOutcome> OnCancelledAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} cancelled", ctx.ServerTaskId);

        // Scripts first, so a task that reaches Cancelled never has an agent script left running (a crash between the two would otherwise orphan it).
        // Safe even from a run that lost the lease: the task is Cancelling, and whichever run owns it cancels the same scripts.
        await CancelInFlightScriptsAsync(ctx, ct).ConfigureAwait(false);

        if (!await TransitionOwnedAsync(ctx, TaskState.Cancelling, TaskState.Cancelled, false, ct).ConfigureAwait(false)) return DeploymentOutcome.NotRecorded;

        await CleanupCheckpointAsync(ctx, ct).ConfigureAwait(false);

        return DeploymentOutcome.Cancelled;
    }

    // The one mechanism every non-cancel outcome writes through. The write is conditional on the row still being in the state the run read; when it is
    // refused because the user's cancel landed in between (the row is now Cancelling), the cancel wins and is completed here. Swallowing the refusal instead
    // would leave the row Cancelling — holding its environment's concurrency slot with agent scripts running — until the stale-task reaper frees it.
    private async Task<DeploymentOutcome> TransitionOrYieldToCancelAsync(DeploymentTaskContext ctx, string fromState, string toState, bool? recordSuccess, CancellationToken ct)
    {
        try
        {
            return await TransitionOwnedAsync(ctx, fromState, toState, recordSuccess, ct).ConfigureAwait(false) ? OutcomeOf(toState) : DeploymentOutcome.NotRecorded;
        }
        catch (ServerTaskStateTransitionException refused) when (IsCancelling(refused.FromState))
        {
            return await CompleteRacingCancelAsync(ctx, toState, ct).ConfigureAwait(false);
        }
    }

    private Task<DeploymentOutcome> CompleteRacingCancelAsync(DeploymentTaskContext ctx, string intendedState, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} was cancelled by the user before its {State} outcome was written; completing the cancel instead", ctx.ServerTaskId, intendedState);

        return OnCancelledAsync(ctx, ct);
    }

    private static bool IsCancelling(string state) => string.Equals(state, TaskState.Cancelling, StringComparison.OrdinalIgnoreCase);

    private static DeploymentOutcome OutcomeOf(string state) => state switch
    {
        TaskState.Success => DeploymentOutcome.Succeeded,
        TaskState.Failed => DeploymentOutcome.Failed,
        TaskState.Cancelled => DeploymentOutcome.Cancelled,
        TaskState.Paused => DeploymentOutcome.Paused,
        _ => DeploymentOutcome.NotRecorded
    };

    // The one place that guarantees "a task that reaches Cancelled has no agent script left running": whichever path led here, and whatever the
    // observer decided when its token fired (it may have chosen "pausing" and left the script alone), every ticket still recorded in flight is
    // cancelled before the checkpoint that holds them is deleted. Idempotent on the agent; best effort and bounded inside the canceller.
    private async Task CancelInFlightScriptsAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        if (inFlightScriptCanceller == null) return;

        await inFlightScriptCanceller.CancelAllAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminal handling for <see cref="Exceptions.DeploymentSuspendedException"/>: leave the task
    /// in <see cref="TaskState.Paused"/> with its checkpoint intact so an operator can resume it.
    ///
    /// <para><b>Why this transitions rather than only logging</b>: most suspend sites set Paused
    /// themselves immediately before throwing (manual intervention, guided failure), so this used
    /// to be a pure log. That made the resulting state an accident of WHICH site threw — a site
    /// that throws without transitioning first would leave the task in whatever state it happened
    /// to be in. An <c>Executing</c> task left that way is worse than any pause: <c>resume</c>
    /// rejects a non-Paused task and <c>cancel</c> only moves it to <c>Cancelling</c>, while the
    /// row keeps occupying the environment's concurrency slot and blocks every other deployment
    /// to that environment. So the outcome is written here, once, for every suspend site.</para>
    ///
    /// <para>See <see cref="PauseIfStillExecutingAsync"/> for which states are written and why
    /// the rest are deliberately left alone.</para>
    /// </summary>
    public Task<DeploymentOutcome> OnPausedAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} paused, checkpoint preserved for resume", ctx.ServerTaskId);

        return PauseIfStillExecutingAsync(ctx, ct);
    }

    /// <summary>
    /// A deployment that exceeds the wall-clock timeout is treated as a pause,
    /// not a failure: the task transitions to <see cref="TaskState.Paused"/> and
    /// its checkpoint is left intact so an operator can resume it (POST
    /// tasks/{id}/resume) once the cause is understood, rather than losing every
    /// already-completed batch and restarting from scratch. We deliberately do
    /// NOT delete the checkpoint (it is the resume point) and do NOT write a
    /// <c>DeploymentCompletion</c> record (a paused deployment has not completed
    /// — the completion is recorded when it later succeeds or fails). The
    /// historical fail-fast behaviour (Failed + checkpoint deleted) remains
    /// available via the <c>SQUID_DEPLOYMENT_TIMEOUT_RESUMABLE</c> escape hatch,
    /// which routes timeouts back through <see cref="OnFailureAsync"/>.
    /// </summary>
    public Task<DeploymentOutcome> OnTimedOutAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Warning(ex, "[Deploy] Task {TaskId} timed out; pausing for resume, checkpoint preserved", ctx.ServerTaskId);

        return PauseIfStillExecutingAsync(ctx, ct);
    }

    /// <summary>
    /// A transient infrastructure failure (a Halibut RPC drop that outlived the
    /// library's own retries, or an agent that went unreachable mid-script) pauses
    /// the deployment rather than failing it: the task transitions to
    /// <see cref="TaskState.Paused"/> with its checkpoint AND in-flight script
    /// pointer preserved, so a resume re-attaches to the still-running script
    /// instead of re-dispatching a duplicate. Like <see cref="OnTimedOutAsync"/> we
    /// deliberately do NOT delete the checkpoint and do NOT write a
    /// <c>DeploymentCompletion</c> record (the deployment has not completed). Pausing on a
    /// transient blip has no env-var opt-out (unlike the timeout's
    /// <c>SQUID_DEPLOYMENT_TIMEOUT_RESUMABLE</c>), because failing fast here would discard
    /// already-completed progress and risk a duplicate run. That is separate from WHICH states
    /// the pause is written from — see <see cref="PauseIfStillExecutingAsync"/>.
    /// </summary>
    public Task<DeploymentOutcome> OnTransientPauseAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Warning(ex, "[Deploy] Task {TaskId} hit a transient infrastructure failure; pausing for resume, checkpoint preserved", ctx.ServerTaskId);

        return PauseIfStillExecutingAsync(ctx, ct);
    }

    /// <summary>
    /// Moves the task to <see cref="TaskState.Paused"/>, but ONLY from
    /// <see cref="TaskState.Executing"/> — the sole legal source of that edge.
    ///
    /// <para>Shared by all three pause outcomes (suspend, timeout, transient) because the
    /// decision is identical for each. A task that already reached a terminal state has recorded
    /// its outcome, and <c>Paused</c> is skipped because the suspend sites that self-transition are
    /// the common case (<c>Paused → Paused</c> is not legal either) — the task is paused all the same,
    /// so that is the outcome reported.</para>
    ///
    /// <para><b>A user's cancel wins, and is completed here.</b> When the row is <c>Cancelling</c> —
    /// read so up front, or because the cancel landed between that read and the conditional write,
    /// which is then refused — the run completes the cancel (scripts cancelled, <c>Cancelled</c>,
    /// checkpoint removed) instead of skipping. Skipping left the row <c>Cancelling</c>: an active
    /// state that keeps holding the environment's concurrency slot, whose only exits need a live
    /// pipeline, so it stayed wedged with its agent scripts running until the stale-task reaper
    /// (default 5 minutes) or an operator freed it.</para>
    /// </summary>
    private async Task<DeploymentOutcome> PauseIfStillExecutingAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var fromState = await ResolveCurrentActiveStateAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        if (IsCancelling(fromState)) return await CompleteRacingCancelAsync(ctx, TaskState.Paused, ct).ConfigureAwait(false);

        if (!string.Equals(fromState, TaskState.Executing, StringComparison.OrdinalIgnoreCase)) return SkipPause(ctx.ServerTaskId, fromState);

        return await TransitionOrYieldToCancelAsync(ctx, TaskState.Executing, TaskState.Paused, null, ct).ConfigureAwait(false);
    }

    // A task that is already Paused (a suspend site paused it itself) is reported Paused; one that already reached a terminal state was resolved by
    // someone else, so this run has nothing to announce.
    private static DeploymentOutcome SkipPause(int serverTaskId, string fromState)
    {
        Log.Information("[Deploy] Task {TaskId} is {State}, not Executing — leaving the state as-is rather than forcing a pause", serverTaskId, fromState);

        return string.Equals(fromState, TaskState.Paused, StringComparison.OrdinalIgnoreCase) ? DeploymentOutcome.Paused : DeploymentOutcome.NotRecorded;
    }

    // Every write this run makes out of a running state is conditional on the run still holding the task's lease: a task another run recovered or resumed
    // is not ours to pause, finish or cancel, and nothing after it (the checkpoint cleanup, auto-deploys) may run on that run's behalf either.
    // A terminal outcome (recordSuccess != null) writes its DeploymentCompletion in the SAME transaction as the state: lifecycle progression and auto-deploy
    // read that row, so a task must never be terminal without it. A run that lost the lease throws before the record is added, so it writes neither.
    // A transient database failure (a dropped connection, a serialization failure) rolls the whole transaction back, so the move is simply tried again, a bounded
    // number of times with each wait and their total capped (ServerTaskTerminalWriteOptions.RetryDelay): otherwise a finished deployment would reach the runner's
    // failure path and be recorded Failed. Each attempt is self-contained (the rolled-back insert is detached, the state write is conditional on the lease), a
    // refused lease is answered inside the attempt and never retried, and once the attempts or the wait budget are used up the failure surfaces as it always did.
    // A failed commit may still have landed on the server: a retry refused because the row is already in the target state is that earlier attempt's write.
    private async Task<bool> TransitionOwnedAsync(DeploymentTaskContext ctx, string fromState, string toState, bool? recordSuccess, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TransitionOwnedOnceAsync(ctx, fromState, toState, recordSuccess, ct).ConfigureAwait(false);
            }
            catch (ServerTaskStateTransitionException refused) when (attempt > 1 && string.Equals(refused.FromState, toState, StringComparison.OrdinalIgnoreCase))
            {
                if (!await EarlierAttemptRecordedAsync(ctx, recordSuccess, ct).ConfigureAwait(false)) throw;

                Log.Information("[Deploy] Task {TaskId} is already {State}: the earlier attempt's commit landed although it was reported as failed; accepting it", ctx.ServerTaskId, toState);

                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && PostgresTransientFailureClassifier.IsTransient(ex) && _terminalWrite.RetryDelay(attempt, waited) is { } delay)
            {
                LogTerminalRetry(ctx, toState, ex, attempt, delay);

                await Task.Delay(delay, ct).ConfigureAwait(false);

                waited += delay;
            }
        }
    }

    // A retry refused because the row is already in the target state is answering this run's own earlier attempt when that attempt's commit landed but its
    // acknowledgement was lost (a connection dropped mid-commit). Its completion record, written in the same transaction, proves it; a pause writes none, so
    // the state alone does. The lease cannot: a terminal or paused row no longer carries one. The retry's own insert never ran (the state write refused it
    // first), so accepting adds no second record.
    private async Task<bool> EarlierAttemptRecordedAsync(DeploymentTaskContext ctx, bool? recordSuccess, CancellationToken ct)
    {
        if (recordSuccess == null || ctx.Deployment == null) return true;

        var expected = recordSuccess.Value ? TaskState.Success : TaskState.Failed;
        var completions = await deploymentCompletionDataProvider.GetDeploymentCompletionsByDeploymentIdAsync(ctx.Deployment.Id, ct).ConfigureAwait(false);

        return completions.Any(c => string.Equals(c.State, expected, StringComparison.OrdinalIgnoreCase));
    }

    private void LogTerminalRetry(DeploymentTaskContext ctx, string toState, Exception failure, int attempt, TimeSpan delay)
    {
        Log.Warning(failure, "[Deploy] Task {TaskId} hit a transient database failure (SQLSTATE {SqlState}) writing its {State} outcome, attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs} ms", ctx.ServerTaskId, PostgresTransientFailureClassifier.SqlStateOf(failure), toState, attempt, _terminalWrite.MaxAttempts, (long)delay.TotalMilliseconds);
    }

    private async Task<bool> TransitionOwnedOnceAsync(DeploymentTaskContext ctx, string fromState, string toState, bool? recordSuccess, CancellationToken ct)
    {
        DeploymentCompletion pending = null;

        try
        {
            await genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
            {
                await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, fromState, toState, ctx.LeaseOwner, cancellationToken).ConfigureAwait(false);

                pending = await AddCompletionIfRequiredAsync(ctx, recordSuccess, cancellationToken).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            return true;
        }
        catch (ServerTaskLeaseNotHeldException ex)
        {
            DetachCompletion(pending);

            Log.Warning("[Deploy] Task {TaskId} is now leased to another run ({Owner}); this run's move {From} -> {To} was refused and nothing else is recorded", ctx.ServerTaskId, ex.CurrentOwner, fromState, toState);

            return false;
        }
        catch
        {
            DetachCompletion(pending);

            throw;
        }
    }

    // The rolled-back insert must not stay tracked: the scoped DbContext is reused by the failure path and the checkpoint cleanup, and their SaveChanges would replay it.
    private void DetachCompletion(DeploymentCompletion completion)
    {
        if (completion != null) deploymentCompletionDataProvider.Detach(completion);
    }

    private async Task<DeploymentCompletion> AddCompletionIfRequiredAsync(DeploymentTaskContext ctx, bool? recordSuccess, CancellationToken ct)
    {
        if (recordSuccess == null || ctx.Deployment == null) return null;

        var deployment = await deploymentDataProvider.GetDeploymentByIdAsync(ctx.Deployment.Id, ct).ConfigureAwait(false);

        var completion = new DeploymentCompletion
        {
            DeploymentId = ctx.Deployment.Id,
            CompletedTime = DateTimeOffset.UtcNow,
            State = recordSuccess.Value ? TaskState.Success : TaskState.Failed,
            SpaceId = deployment?.SpaceId ?? 1,
            SequenceNumber = 0
        };

        await deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion, false, ct).ConfigureAwait(false);

        return completion;
    }

    private async Task<string> ResolveCurrentActiveStateAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await serverTaskService.GetTaskAsync(serverTaskId, ct).ConfigureAwait(false);

        return task?.State ?? TaskState.Executing;
    }

    private async Task CleanupCheckpointAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        try
        {
            await checkpointService.DeleteAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to cleanup checkpoint for task {TaskId}, continuing", ctx.ServerTaskId);
        }
    }

    private async Task TriggerAutoDeploymentsAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        try
        {
            if (ctx.Deployment == null) return;

            await autoDeployService.TriggerAutoDeploymentsAsync(ctx.Deployment.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Auto-deploy trigger failed for deployment {DeploymentId}, continuing", ctx.Deployment?.Id);
        }
    }
}
