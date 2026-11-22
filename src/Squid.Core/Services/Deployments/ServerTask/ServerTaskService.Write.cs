using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;
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
            await ClaimOrFenceAsync(task, TaskState.Pending, ct).ConfigureAwait(false);
        else if (isResumed)
            await ClaimOrFenceAsync(task, TaskState.Paused, ct).ConfigureAwait(false);
        else if (string.Equals(task.State, TaskState.Executing, StringComparison.OrdinalIgnoreCase))
            await FenceAlreadyExecutingAsync(task, ct).ConfigureAwait(false);
        else
            throw new ServerTaskStateTransitionException(task.State, TaskState.Executing);

        task.State = TaskState.Executing;
        task.StartTime ??= DateTimeOffset.UtcNow;

        return new StartExecutingResult(task, isResumed);
    }

    // Two deliveries can both read the task as Pending/Paused; only one CAS wins. The loser finds the row Executing — the winner's — and must take
    // the same fenced path as any duplicate delivery to a running task, never fall into a failure path that would fail the winner's live deployment.
    // Any other state after a lost CAS keeps the original transition error.
    private async Task ClaimOrFenceAsync(Persistence.Entities.Deployments.ServerTask task, string from, CancellationToken ct)
    {
        try
        {
            await _serverTaskDataProvider.TransitionStateAsync(task.Id, from, TaskState.Executing, ct).ConfigureAwait(false);
        }
        catch (ServerTaskStateTransitionException ex) when (string.Equals(ex.FromState, TaskState.Executing, StringComparison.OrdinalIgnoreCase))
        {
            await FenceAlreadyExecutingAsync(task, ct).ConfigureAwait(false);
        }
    }

    // A task delivered while already Executing is either a dead owner's task being re-run (Hangfire retry) or a duplicate delivery to a live one.
    // The lease tells them apart in one atomic UPDATE: it is taken only when nobody holds it or its holder went silent — never because the
    // holder is this same process, which would let a duplicate delivery in the same pod run beside the live pipeline.
    private async Task FenceAlreadyExecutingAsync(Persistence.Entities.Deployments.ServerTask task, CancellationToken ct)
    {
        var rows = await _serverTaskLeaseDataProvider.ClaimRunningAsync(task.Id, ServerTaskLeaseOwner.Current, _leaseStale, ct).ConfigureAwait(false);

        if (rows == 0)
            throw new ServerTaskOwnedByLiveProcessException(task.Id, await ReadLeaseOwnerAsync(task, ct).ConfigureAwait(false));
    }

    // The owner named in the refusal is read fresh: the row the caller loaded may predate the winner's claim.
    private async Task<string> ReadLeaseOwnerAsync(Persistence.Entities.Deployments.ServerTask task, CancellationToken ct)
    {
        var current = await _serverTaskDataProvider.GetServerTaskByIdNoTrackingAsync(task.Id, ct).ConfigureAwait(false);

        return current?.LeaseOwner ?? task.LeaseOwner;
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
