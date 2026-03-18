using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

/// <summary>
/// Thread-safe writer for deployment activity nodes and task logs.
/// Each operation creates its own DbContext, allowing concurrent writes
/// from parallel target execution without DbContext contention.
/// </summary>
public interface IDeploymentLogWriter : ISingletonDependency
{
    Task<ActivityLog> AddActivityNodeAsync(int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct = default);

    Task UpdateActivityNodeStatusAsync(long nodeId, DeploymentActivityLogNodeStatus status, DateTimeOffset? endedAt = null, CancellationToken ct = default);

    Task AddLogAsync(int taskId, long sequenceNumber, ServerTaskLogCategory category, string message, string source, long? activityNodeId = null, DateTimeOffset? occurredAt = null, CancellationToken ct = default);

    Task AddLogsAsync(int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken ct = default);

    Task<List<ActivityLog>> GetTreeByTaskIdAsync(int serverTaskId, CancellationToken ct = default);
}
