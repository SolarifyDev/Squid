using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.Deployments.ServerTask;

public partial class ServerTaskService
{
    public async Task<StartExecutingResult> StartExecutingAsync(int taskId, CancellationToken ct = default)
    {
        var task = await _serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(taskId, ct).ConfigureAwait(false);

        if (task == null)
            throw new ServerTaskNotFoundException(taskId);

        var isResumed = string.Equals(task.State, TaskState.Paused, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(task.State, TaskState.Pending, StringComparison.OrdinalIgnoreCase))
            await _serverTaskDataProvider.TransitionStateAsync(task.Id, TaskState.Pending, TaskState.Executing, ct).ConfigureAwait(false);
        else if (isResumed)
            await _serverTaskDataProvider.TransitionStateAsync(task.Id, TaskState.Paused, TaskState.Executing, ct).ConfigureAwait(false);
        else if (!string.Equals(task.State, TaskState.Executing, StringComparison.OrdinalIgnoreCase))
            throw new ServerTaskStateTransitionException(task.State, TaskState.Executing);

        task.State = TaskState.Executing;
        task.StartTime ??= DateTimeOffset.UtcNow;

        return new StartExecutingResult(task, isResumed);
    }

    public Task AddLogAsync(int taskId, long sequenceNumber, ServerTaskLogCategory category, string message, string source, long? activityNodeId = null, DateTimeOffset? occurredAt = null, string detail = null, CancellationToken ct = default)
    {
        return _serverTaskLogDataProvider.AddLogAsync(new Persistence.Entities.Deployments.ServerTaskLog
        {
            ServerTaskId = taskId,
            ActivityNodeId = activityNodeId,
            Category = category,
            MessageText = message,
            Detail = detail,
            Source = source,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            SequenceNumber = sequenceNumber
        }, ct: ct);
    }
    
    public Task AddLogsAsync(int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken ct = default)
    {
        if (entries == null || entries.Count == 0)
            return Task.CompletedTask;

        var logs = entries.Select(entry => new Persistence.Entities.Deployments.ServerTaskLog
        {
            ServerTaskId = taskId,
            ActivityNodeId = entry.ActivityNodeId,
            Category = entry.Category,
            MessageText = entry.MessageText,
            Detail = entry.Detail,
            Source = entry.Source,
            OccurredAt = entry.OccurredAt ?? DateTimeOffset.UtcNow,
            SequenceNumber = entry.SequenceNumber
        }).ToList();

        return _serverTaskLogDataProvider.AddLogsAsync(logs, ct: ct);
    }
    
    public Task<Persistence.Entities.Deployments.ActivityLog> AddActivityNodeAsync(int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct = default)
    {
        return _activityLogDataProvider.AddNodeAsync(
            new Persistence.Entities.Deployments.ActivityLog
            {
                ServerTaskId = taskId,
                ParentId = parentId,
                Name = name,
                NodeType = nodeType,
                Status = status,
                StartedAt = DateTimeOffset.UtcNow,
                SortOrder = sortOrder
            }, ct: ct);
    }

    public Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, CancellationToken ct = default) => _serverTaskDataProvider.TransitionStateAsync(taskId, expectedCurrentState, newState, ct);

    public Task UpdateActivityNodeStatusAsync(long nodeId, DeploymentActivityLogNodeStatus status, DateTimeOffset? endedAt = null, CancellationToken ct = default) => _activityLogDataProvider.UpdateNodeStatusAsync(nodeId, status, endedAt, ct: ct);

    public Task SetHasPendingInterruptionsAsync(int serverTaskId, bool hasPending, CancellationToken ct = default) => _serverTaskDataProvider.SetHasPendingInterruptionsAsync(serverTaskId, hasPending, ct);
}
