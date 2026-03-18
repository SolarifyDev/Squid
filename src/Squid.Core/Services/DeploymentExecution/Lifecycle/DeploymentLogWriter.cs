using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

/// <summary>
/// Thread-safe log writer that creates a fresh DbContext per operation.
/// Each write gets its own connection from the pool — no DbContext contention
/// when called concurrently from parallel target execution.
/// </summary>
public sealed class DeploymentLogWriter : IDeploymentLogWriter
{
    private readonly DbContextOptions<SquidDbContext> _dbOptions;

    public DeploymentLogWriter(DbContextOptions<SquidDbContext> dbOptions)
    {
        _dbOptions = dbOptions;
    }

    public async Task<ActivityLog> AddActivityNodeAsync(int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct = default)
    {
        var node = new ActivityLog
        {
            ServerTaskId = taskId,
            ParentId = parentId,
            Name = name,
            NodeType = nodeType,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            SortOrder = sortOrder
        };

        await using var db = CreateContext();
        await db.AddAsync(node, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return node;
    }

    public async Task UpdateActivityNodeStatusAsync(long nodeId, DeploymentActivityLogNodeStatus status, DateTimeOffset? endedAt = null, CancellationToken ct = default)
    {
        await using var db = CreateContext();

        await db.Set<ActivityLog>()
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, status)
                .SetProperty(n => n.EndedAt, endedAt)
                .SetProperty(n => n.LastModifiedDate, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    }

    public async Task AddLogAsync(int taskId, long sequenceNumber, ServerTaskLogCategory category, string message, string source, long? activityNodeId = null, DateTimeOffset? occurredAt = null, CancellationToken ct = default)
    {
        var log = new ServerTaskLog
        {
            ServerTaskId = taskId,
            ActivityNodeId = activityNodeId,
            Category = category,
            MessageText = message,
            Source = source,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            SequenceNumber = sequenceNumber
        };

        await using var db = CreateContext();
        await db.AddAsync(log, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddLogsAsync(int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken ct = default)
    {
        if (entries == null || entries.Count == 0) return;

        var logs = entries.Select(entry => new ServerTaskLog
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

        await using var db = CreateContext();
        await db.AddRangeAsync(logs, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ActivityLog>> GetTreeByTaskIdAsync(int serverTaskId, CancellationToken ct = default)
    {
        await using var db = CreateContext();

        return await db.Set<ActivityLog>()
            .AsNoTracking()
            .Where(n => n.ServerTaskId == serverTaskId)
            .OrderBy(n => n.SortOrder)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private SquidDbContext CreateContext() => new(_dbOptions);
}
