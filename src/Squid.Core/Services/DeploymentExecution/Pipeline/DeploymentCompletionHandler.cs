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

        Log.Information("Task {TaskId} completed successfully", ctx.ServerTaskId);
    }

    public async Task OnFailureAsync(DeploymentTaskContext ctx, Exception ex, CancellationToken ct)
    {
        Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", ctx.ServerTaskId, ex.Message);

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
        Log.Information("Task {TaskId} cancelled", ctx.ServerTaskId);

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
        Log.Information("Task {TaskId} paused, checkpoint preserved for resume", ctx.ServerTaskId);

        return Task.CompletedTask;
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

        Log.Information("Recorded deployment completion for deployment {DeploymentId}: {Status}", ctx.Deployment.Id, success ? "Success" : "Failed");
    }

    private async Task CleanupCheckpointAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        try
        {
            await checkpointService.DeleteAsync(ctx.ServerTaskId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cleanup checkpoint for task {TaskId}, continuing", ctx.ServerTaskId);
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
            Log.Warning(ex, "Auto-deploy trigger failed for deployment {DeploymentId}, continuing", ctx.Deployment?.Id);
        }
    }
}
