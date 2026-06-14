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

    public Task OnPausedAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("[Deploy] Task {TaskId} paused, checkpoint preserved for resume", ctx.ServerTaskId);

        return Task.CompletedTask;
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

        var fromState = await ResolveCurrentActiveStateAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        await genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, fromState, TaskState.Paused, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A transient infrastructure failure (a Halibut RPC drop that outlived the
    /// library's own retries, or an agent that went unreachable mid-script) pauses
    /// the deployment rather than failing it: the task transitions to
    /// <see cref="TaskState.Paused"/> with its checkpoint AND in-flight script
    /// pointer preserved, so a resume re-attaches to the still-running script
    /// instead of re-dispatching a duplicate. Like <see cref="OnTimedOutAsync"/> we
    /// deliberately do NOT delete the checkpoint and do NOT write a
    /// <c>DeploymentCompletion</c> record (the deployment has not completed). This is
    /// unconditional — there is no opt-out, because failing fast on a transient blip
    /// would discard already-completed progress and risk a duplicate run.
    /// </summary>
    public async Task OnTransientPauseAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Warning(ex, "[Deploy] Task {TaskId} hit a transient infrastructure failure; pausing for resume, checkpoint preserved", ctx.ServerTaskId);

        var fromState = await ResolveCurrentActiveStateAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);

        await genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, fromState, TaskState.Paused, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
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
