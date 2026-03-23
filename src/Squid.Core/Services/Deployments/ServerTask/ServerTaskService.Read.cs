using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask;

public partial class ServerTaskService
{
    public async Task<ServerTaskSummaryDto> GetTaskAsync(int taskId, CancellationToken ct = default)
    {
        var task = await _serverTaskDataProvider.GetServerTaskByIdAsync(taskId, ct).ConfigureAwait(false);
        
        return MapTask(task);
    }

    public async Task<ServerTaskDetailsDto> GetTaskDetailsAsync(int taskId, bool? verbose = null, int? tail = null, CancellationToken ct = default)
    {
        _ = verbose;

        var task = await _serverTaskDataProvider.GetServerTaskByIdAsync(taskId, ct).ConfigureAwait(false);
        
        if (task == null) return null;

        var tailPerNode = NormalizeTail(tail);
        var flatNodes = await _activityLogDataProvider.GetTreeByTaskIdAsync(taskId, ct).ConfigureAwait(false);
        var logs = new List<Persistence.Entities.Deployments.ServerTaskLog>();

        foreach (var node in flatNodes)
        {
            var nodeLogs = await _serverTaskLogDataProvider
                .GetLatestLogsByTaskAndNodeAsync(taskId, node.Id, tailPerNode, ct).ConfigureAwait(false);
            
            logs.AddRange(nodeLogs);
        }

        var unscopedLogs = await _serverTaskLogDataProvider
            .GetLatestUnscopedLogsByTaskAsync(taskId, tailPerNode, ct).ConfigureAwait(false);
        
        logs.AddRange(unscopedLogs);

        return new ServerTaskDetailsDto
        {
            Task = MapTask(task),
            ActivityLogs = BuildTree(flatNodes, logs, tailPerNode),
            PhysicalLogSize = await _serverTaskLogDataProvider.GetLogCountByTaskIdAsync(taskId, ct).ConfigureAwait(false),
            Progress = BuildProgress(task, flatNodes)
        };
    }

    public async Task<ServerTaskLogPageDto> GetTaskLogsAsync(int taskId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default)
    {
        var pageSize = NormalizePageSize(take);
        
        var logs = await _serverTaskLogDataProvider
            .GetLogsByTaskIdAfterSequenceAsync(taskId, afterSequenceNumber, pageSize + 1, ct).ConfigureAwait(false);

        return BuildLogPage(logs, pageSize);
    }

    public async Task<ServerTaskLogPageDto> GetTaskNodeLogsAsync(int taskId, long nodeId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default)
    {
        var pageSize = NormalizePageSize(take);
        
        var logs = await _serverTaskLogDataProvider
            .GetLogsByTaskAndNodeAfterSequenceAsync(taskId, nodeId, afterSequenceNumber, pageSize + 1, ct).ConfigureAwait(false);

        return BuildLogPage(logs, pageSize);
    }

    public async Task<(int TotalCount, List<ServerTaskSummaryDto> Items)> GetTaskListAsync(int projectId, string state = null, int pageIndex = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var skip = (Math.Max(pageIndex, 1) - 1) * pageSize;

        var (totalCount, tasks) = await _serverTaskDataProvider
            .GetServerTasksByProjectAsync(projectId, state, skip, pageSize, ct).ConfigureAwait(false);

        return (totalCount, tasks.Select(MapTask).ToList());
    }

    private static ServerTaskLogPageDto BuildLogPage(List<Persistence.Entities.Deployments.ServerTaskLog> logs, int pageSize)
    {
        var hasMore = logs.Count > pageSize;
        var pageItems = hasMore ? logs.Take(pageSize).ToList() : logs;
        var lastSequence = pageItems.Count == 0 ? (long?)null : pageItems[^1].SequenceNumber;

        return new ServerTaskLogPageDto
        {
            Items = pageItems.Select(MapLog).ToList(),
            HasMore = hasMore,
            LastSequenceNumber = lastSequence,
            NextAfterSequenceNumber = lastSequence
        };
    }
}
