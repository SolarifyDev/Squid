using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private Task CreateTaskActivityNodeAsync(CancellationToken ct)
        => _lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), ct);

    private async Task RecordSuccessAsync(CancellationToken ct)
    {
        await _lifecycle.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), ct).ConfigureAwait(false);
        await RecordCompletionAsync(true, "Deployment completed successfully");

        await _genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await _serverTaskService.TransitionStateAsync(_ctx.ServerTaskId, TaskState.Executing, TaskState.Success, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await TriggerAutoDeploymentsAsync(ct).ConfigureAwait(false);

        Log.Information("Task {TaskId} completed successfully", _ctx.ServerTaskId);
    }

    private async Task TriggerAutoDeploymentsAsync(CancellationToken ct)
    {
        try
        {
            if (_ctx.Deployment == null) return;

            await _autoDeployService.TriggerAutoDeploymentsAsync(_ctx.Deployment.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-deploy trigger failed for deployment {DeploymentId}, continuing", _ctx.Deployment?.Id);
        }
    }

    private async Task RecordFailureAsync(Exception ex, CancellationToken ct)
    {
        Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", _ctx.ServerTaskId, ex.Message);

        await _lifecycle.EmitAsync(new DeploymentFailedEvent(new DeploymentEventContext { Exception = ex }), ct).ConfigureAwait(false);

        if (_ctx.Deployment != null)
            await RecordCompletionAsync(false, ex.Message);

        await _genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await _serverTaskService.TransitionStateAsync(_ctx.ServerTaskId, TaskState.Executing, TaskState.Failed, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task RecordCompletionAsync(bool success, string message)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(_ctx.Deployment.Id).ConfigureAwait(false);

        var completion = new Persistence.Entities.Deployments.DeploymentCompletion
        {
            DeploymentId = _ctx.Deployment.Id,
            CompletedTime = DateTimeOffset.UtcNow,
            State = success ? TaskState.Success : TaskState.Failed,
            SpaceId = deployment?.SpaceId ?? 1,
            SequenceNumber = 0
        };

        await _deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion).ConfigureAwait(false);

        Log.Information("Recorded deployment completion for deployment {DeploymentId}: {Status}", _ctx.Deployment.Id, success ? "Success" : "Failed");
    }
}
