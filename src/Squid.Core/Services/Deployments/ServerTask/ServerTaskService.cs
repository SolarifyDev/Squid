using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask;

public record StartExecutingResult(Persistence.Entities.Deployments.ServerTask Task, bool IsResumed);

public interface IServerTaskService : IScopedDependency
{
    Task<StartExecutingResult> StartExecutingAsync(int taskId, CancellationToken ct = default);

    Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, CancellationToken ct = default);

    Task<Persistence.Entities.Deployments.ActivityLog> AddActivityNodeAsync(int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct = default);

    Task UpdateActivityNodeStatusAsync(long nodeId, DeploymentActivityLogNodeStatus status, DateTimeOffset? endedAt = null, CancellationToken ct = default);

    Task AddLogAsync(int taskId, long sequenceNumber, ServerTaskLogCategory category, string message, string source, long? activityNodeId = null, DateTimeOffset? occurredAt = null, string detail = null, CancellationToken ct = default);

    Task AddLogsAsync(int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken ct = default);

    Task<ServerTaskSummaryDto> GetTaskAsync(int taskId, CancellationToken ct = default);

    Task<ServerTaskDetailsDto> GetTaskDetailsAsync(int taskId, bool? verbose = null, int? tail = null, CancellationToken ct = default);

    Task<ServerTaskLogPageDto> GetTaskLogsAsync(int taskId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default);

    Task<ServerTaskLogPageDto> GetTaskNodeLogsAsync(int taskId, long nodeId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default);

    Task SetHasPendingInterruptionsAsync(int serverTaskId, bool hasPending, CancellationToken ct = default);
}

public class ServerTaskLogWriteEntry
{
    public long SequenceNumber { get; set; }

    public ServerTaskLogCategory Category { get; set; }

    public string MessageText { get; set; }

    public string Detail { get; set; }

    public string Source { get; set; }

    public DateTimeOffset? OccurredAt { get; set; }

    public long? ActivityNodeId { get; set; }
}

public partial class ServerTaskService : IServerTaskService
{
    private const int DefaultTailPerNode = 50;
    private const int DefaultPageSize = 500;
    private const int MaxPageSize = 2000;

    private readonly IServerTaskDataProvider _serverTaskDataProvider;
    private readonly IActivityLogDataProvider _activityLogDataProvider;
    private readonly IServerTaskLogDataProvider _serverTaskLogDataProvider;

    public ServerTaskService(
        IServerTaskDataProvider serverTaskDataProvider,
        IActivityLogDataProvider activityLogDataProvider,
        IServerTaskLogDataProvider serverTaskLogDataProvider)
    {
        _serverTaskDataProvider = serverTaskDataProvider;
        _activityLogDataProvider = activityLogDataProvider;
        _serverTaskLogDataProvider = serverTaskLogDataProvider;
    }

    private static int NormalizePageSize(int? take)
    {
        if (!take.HasValue || take.Value <= 0)
            return DefaultPageSize;

        return Math.Min(take.Value, MaxPageSize);
    }

    private static int NormalizeTail(int? tail)
    {
        if (!tail.HasValue || tail.Value <= 0)
            return DefaultTailPerNode;

        return Math.Min(tail.Value, MaxPageSize);
    }
}
