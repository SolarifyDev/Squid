using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.DeploymentExecution.Pipeline;

public sealed class DeploymentCompletionHandler(
    IGenericDataProvider genericDataProvider,
    IServerTaskService serverTaskService,
    IDeploymentDataProvider deploymentDataProvider,
    IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
    IAutoDeployService autoDeployService,
    IDeploymentCheckpointService checkpointService) : IDeploymentCompletionHandler
{
    public async Task OnSuccessAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        await RecordCompletionAsync(ctx, true, "Deployment completed successfully").ConfigureAwait(false);

        await genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, TaskState.Executing, TaskState.Success, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await CleanupCheckpointAsync(ctx, ct).ConfigureAwait(false);
        await TriggerAutoDeploymentsAsync(ctx, ct).ConfigureAwait(false);

        Log.Information("[Deploy] Task {TaskId} completed successfully", ctx.ServerTaskId);
    }

    public async Task OnFailureAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Error(ex, "[Deploy] Task {TaskId} failed", ctx.ServerTaskId);

        if (ctx.Deployment != null)
            await RecordCompletionAsync(ctx, false, ex.Message).ConfigureAwait(false);

        var fromState = await ResolveCurrentActiveStateAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        await genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, fromState, TaskState.Failed, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await CleanupCheckpointAsync(ctx, ct).ConfigureAwait(false);
    }

    public async Task OnCancelledAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} cancelled", ctx.ServerTaskId);

        if (ctx.Deployment != null)
            await RecordCompletionAsync(ctx, false, "Deployment was cancelled").ConfigureAwait(false);

        await genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, TaskState.Cancelling, TaskState.Cancelled, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await CleanupCheckpointAsync(ctx, ct).ConfigureAwait(false);
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
    public async Task OnPausedAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} paused, checkpoint preserved for resume", ctx.ServerTaskId);

        await PauseIfStillExecutingAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);
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
    public async Task OnTimedOutAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Warning(ex, "[Deploy] Task {TaskId} timed out; pausing for resume, checkpoint preserved", ctx.ServerTaskId);

        await PauseIfStillExecutingAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);
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
    public async Task OnTransientPauseAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Warning(ex, "[Deploy] Task {TaskId} hit a transient infrastructure failure; pausing for resume, checkpoint preserved", ctx.ServerTaskId);

        await PauseIfStillExecutingAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the task to <see cref="TaskState.Paused"/>, but ONLY from
    /// <see cref="TaskState.Executing"/> — the sole legal source of that edge.
    ///
    /// <para>Shared by all three pause outcomes (suspend, timeout, transient) because the
    /// decision is identical for each. Skipping is the RIGHT semantic, not merely the safe one:
    /// a cancel that lands mid-pause should win, and a task that already reached a terminal
    /// state has recorded its outcome. <c>Paused</c> is skipped because the suspend sites that
    /// self-transition are the common case and <c>Paused → Paused</c> is not legal either.</para>
    ///
    /// <para><b>What this does NOT do.</b> It does not rescue a task that is already
    /// <c>Cancelling</c>. A blind transition threw
    /// <see cref="ServerTaskStateTransitionException"/> from
    /// <c>TaskState.EnsureValidTransition</c> — the first statement of
    /// <c>TransitionStateAsync</c>, before any SQL — and the runner's <c>SafeCompleteAsync</c>
    /// swallowed it, so the row stayed <c>Cancelling</c> with an empty transaction rolled back.
    /// The guarded skip leaves it <c>Cancelling</c> too. The database outcome is byte-identical;
    /// what changes is that an expected race is no longer signalled by an exception.</para>
    ///
    /// <para>That row is genuinely stuck: <c>Cancelling</c> counts as an active state so it
    /// keeps holding the environment's concurrency slot, its only edges out
    /// (<c>Cancelled</c>, <c>Failed</c>) need a live pipeline, re-cancel no-ops and resume
    /// rejects it, and there is no reaper. Freeing it needs cross-pod cancel propagation or a
    /// stale-active-task reaper — neither of which exists, and both out of scope here. The same
    /// wedge reaches the success path, which transitions from a hardcoded <c>Executing</c>.
    /// The <c>Cancelling</c> skip is therefore logged at Warning: it is the only remaining
    /// signal that an environment just lost a concurrency slot with no automatic recovery.</para>
    /// </summary>
    private async Task PauseIfStillExecutingAsync(int serverTaskId, CancellationToken ct)
    {
        var fromState = await ResolveCurrentActiveStateAsync(serverTaskId, ct).ConfigureAwait(false);

        if (!string.Equals(fromState, TaskState.Executing, StringComparison.OrdinalIgnoreCase))
        {
            LogSkippedPause(serverTaskId, fromState);
            return;
        }

        await genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await serverTaskService.TransitionStateAsync(serverTaskId, TaskState.Executing, TaskState.Paused, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A skipped pause is routine for <c>Paused</c> and for a terminal task, but for
    /// <c>Cancelling</c> it means the task is wedged: it holds the environment's concurrency
    /// slot and nothing will free it. Before the guard, that case at least surfaced as an
    /// Error from the swallowed transition exception; logging it at Warning keeps an alert on
    /// the only path that indefinitely blocks an environment, without making the routine
    /// <c>Paused</c> case noisy.
    /// </summary>
    private static void LogSkippedPause(int serverTaskId, string fromState)
    {
        if (string.Equals(fromState, TaskState.Cancelling, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[Deploy] Task {TaskId} is Cancelling, not Executing — skipping the pause so the cancel wins. The task stays Cancelling and keeps holding its environment's concurrency slot; there is no automatic recovery for this state", serverTaskId);
            return;
        }

        Log.Information("[Deploy] Task {TaskId} is {State}, not Executing — leaving the state as-is rather than forcing a pause", serverTaskId, fromState);
    }

    private async Task<string> ResolveCurrentActiveStateAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await serverTaskService.GetTaskAsync(serverTaskId, ct).ConfigureAwait(false);

        return task?.State ?? TaskState.Executing;
    }

    private async Task RecordCompletionAsync(DeploymentTaskContext ctx, bool success, string message)
    {
        var deployment = await deploymentDataProvider.GetDeploymentByIdAsync(ctx.Deployment.Id).ConfigureAwait(false);

        var completion = new Persistence.Entities.Deployments.DeploymentCompletion
        {
            DeploymentId = ctx.Deployment.Id,
            CompletedTime = DateTimeOffset.UtcNow,
            State = success ? TaskState.Success : TaskState.Failed,
            SpaceId = deployment?.SpaceId ?? 1,
            SequenceNumber = 0
        };

        await deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion).ConfigureAwait(false);
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
