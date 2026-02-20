using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private async Task CreateTaskActivityNodeAsync(CancellationToken ct)
    {
        _ctx.TaskActivityNode = await _activityLogDataProvider.AddNodeAsync(
            new Persistence.Entities.Deployments.ActivityLog
            {
                ServerTaskId = _ctx.Task.Id,
                Name = $"Deploy {_ctx.Deployment?.Name ?? "Unknown"}",
                NodeType = "Task",
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = 0
            }, ct: ct).ConfigureAwait(false);
    }

    private async Task RecordSuccessAsync(CancellationToken ct)
    {
        await RecordCompletionAsync(true, "Deployment completed successfully");

        if (_ctx.TaskActivityNode != null)
            await _activityLogDataProvider.UpdateNodeStatusAsync(
                _ctx.TaskActivityNode.Id, "Success", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

        await _genericDataProvider.ExecuteInTransactionAsync(
            async cancellationToken =>
            {
                await _serverTaskDataProvider.TransitionStateAsync(
                    _ctx.Task.Id, TaskState.Executing, TaskState.Success,
                    cancellationToken).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        Log.Information("Task {TaskId} completed successfully", _ctx.Task.Id);
    }

    private async Task RecordFailureAsync(int serverTaskId, Exception ex, CancellationToken ct)
    {
        Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", serverTaskId, ex.Message);

        if (_ctx.TaskActivityNode != null)
            await _activityLogDataProvider.UpdateNodeStatusAsync(
                _ctx.TaskActivityNode.Id, "Failed", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

        await PersistTaskLogAsync(serverTaskId, "Error", ex.Message, "System", ct);

        if (_ctx.Deployment != null)
            await RecordCompletionAsync(false, ex.Message);

        await _genericDataProvider.ExecuteInTransactionAsync(
            async cancellationToken =>
            {
                await _serverTaskDataProvider.TransitionStateAsync(
                    serverTaskId, TaskState.Executing, TaskState.Failed,
                    cancellationToken).ConfigureAwait(false);
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

        Log.Information(
            "Recorded deployment completion for deployment {DeploymentId}: {Status}",
            _ctx.Deployment.Id, success ? "Success" : "Failed");
    }

    private async Task<Persistence.Entities.Deployments.ActivityLog> CreateActivityNodeAsync(
        int serverTaskId, long? parentId, string name, string nodeType,
        string status, int sortOrder, CancellationToken ct)
    {
        try
        {
            return await _activityLogDataProvider.AddNodeAsync(
                new Persistence.Entities.Deployments.ActivityLog
                {
                    ServerTaskId = serverTaskId,
                    ParentId = parentId,
                    Name = name,
                    NodeType = nodeType,
                    Status = status,
                    StartedAt = DateTimeOffset.UtcNow,
                    SortOrder = sortOrder
                }, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create activity log node: {Name}", name);
            return null;
        }
    }

    private async Task PersistTaskLogAsync(int serverTaskId, string category, string message,
        string source, CancellationToken ct)
    {
        try
        {
            var seq = _ctx.NextLogSequence();
            await _serverTaskLogDataProvider.AddLogAsync(new Persistence.Entities.Deployments.ServerTaskLog
            {
                ServerTaskId = serverTaskId,
                Category = category,
                MessageText = message,
                Source = source,
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = seq
            }, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist task log entry");
        }
    }

    private async Task PersistTaskLogsAsync(int serverTaskId, List<ProcessOutput> logs,
        string source, CancellationToken ct)
    {
        try
        {
            var entries = logs.Select(log => new Persistence.Entities.Deployments.ServerTaskLog
            {
                ServerTaskId = serverTaskId,
                Category = log.Source == ProcessOutputSource.StdErr ? "Error" : "Info",
                MessageText = log.Text,
                Source = source,
                OccurredAt = log.Occurred,
                SequenceNumber = _ctx.NextLogSequence()
            }).ToList();

            await _serverTaskLogDataProvider.AddLogsAsync(entries, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist task log batch");
        }
    }
}
