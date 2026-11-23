using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask;

public record StartExecutingResult(Persistence.Entities.Deployments.ServerTask Task, bool IsResumed);

public interface IServerTaskService : IScopedDependency
{
    Task<StartExecutingResult> StartExecutingAsync(int taskId, CancellationToken ct = default);

    /// <summary>Claims the task for the pipeline RUN named by <paramref name="leaseOwner"/>: the claim stamps that run's lease, and every later write the run makes is conditional on holding it.</summary>
    Task<StartExecutingResult> StartExecutingAsync(int taskId, string leaseOwner, CancellationToken ct = default);

    Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, CancellationToken ct = default);

    /// <summary>A state write made by one pipeline run: out of Executing/Cancelling it applies only while <paramref name="leaseOwner"/> still holds the lease (otherwise <see cref="Exceptions.ServerTaskLeaseNotHeldException"/>).</summary>
    Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, string leaseOwner, CancellationToken ct = default);

    Task<Persistence.Entities.Deployments.ActivityLog> AddActivityNodeAsync(int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct = default);

    Task UpdateActivityNodeStatusAsync(long nodeId, DeploymentActivityLogNodeStatus status, DateTimeOffset? endedAt = null, CancellationToken ct = default);

    Task AddLogAsync(int taskId, long sequenceNumber, ServerTaskLogCategory category, string message, string source, long? activityNodeId = null, DateTimeOffset? occurredAt = null, string detail = null, CancellationToken ct = default);

    Task AddLogsAsync(int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken ct = default);

    Task<ServerTaskSummaryDto> GetTaskAsync(int taskId, CancellationToken ct = default);

    Task<ServerTaskDetailsDto> GetTaskDetailsAsync(int taskId, bool? verbose = null, int? tail = null, CancellationToken ct = default);

    Task<ServerTaskLogPageDto> GetTaskLogsAsync(int taskId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default);

    Task<ServerTaskLogPageDto> GetTaskNodeLogsAsync(int taskId, long nodeId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default);

    Task SetHasPendingInterruptionsAsync(int serverTaskId, bool hasPending, CancellationToken ct = default);

    Task<bool> IsImportedAsync(int serverTaskId, CancellationToken ct = default);

    Task<(int TotalCount, List<ServerTaskSummaryDto> Items)> GetTaskListAsync(int projectId, string state = null, int pageIndex = 1, int pageSize = 30, CancellationToken ct = default);
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
    private readonly IServerTaskLeaseDataProvider _serverTaskLeaseDataProvider;
    private readonly TimeSpan _leaseStale;

    public ServerTaskService(
        IServerTaskDataProvider serverTaskDataProvider,
        IActivityLogDataProvider activityLogDataProvider,
        IServerTaskLogDataProvider serverTaskLogDataProvider,
        IServerTaskLeaseDataProvider serverTaskLeaseDataProvider,
        ServerTaskSetting serverTaskSetting)
    {
        _leaseStale = ServerTaskLeaseOptions.From(serverTaskSetting).Stale;
        _serverTaskLeaseDataProvider = serverTaskLeaseDataProvider;
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
