using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

/// <summary>
/// Thread-safe log writer with Channel-based buffering for task logs.
/// AddLogAsync/AddLogsAsync enqueue to an unbounded Channel and return immediately.
/// A background task batch-writes to DB every 300ms.
/// Activity node operations remain synchronous (must return IDs / timely status updates).
/// </summary>
public sealed class DeploymentLogWriter : IDeploymentLogWriter, IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(300);

    private readonly DbContextOptions<SquidDbContext> _dbOptions;
    private readonly System.Threading.Channels.Channel<ServerTaskLog> _channel = System.Threading.Channels.Channel.CreateUnbounded<ServerTaskLog>(new System.Threading.Channels.UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _backgroundTask;

    public DeploymentLogWriter(DbContextOptions<SquidDbContext> dbOptions)
    {
        _dbOptions = dbOptions;
        _backgroundTask = BackgroundFlushLoopAsync(_cts.Token);
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

    public Task AddLogAsync(int taskId, long sequenceNumber, ServerTaskLogCategory category, string message, string source, long? activityNodeId = null, DateTimeOffset? occurredAt = null, CancellationToken ct = default)
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

        _channel.Writer.TryWrite(log);

        return Task.CompletedTask;
    }

    public Task AddLogsAsync(int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken ct = default)
    {
        if (entries == null || entries.Count == 0) return Task.CompletedTask;

        foreach (var entry in entries)
        {
            var log = new ServerTaskLog
            {
                ServerTaskId = taskId,
                ActivityNodeId = entry.ActivityNodeId,
                Category = entry.Category,
                MessageText = entry.MessageText,
                Detail = entry.Detail,
                Source = entry.Source,
                OccurredAt = entry.OccurredAt ?? DateTimeOffset.UtcNow,
                SequenceNumber = entry.SequenceNumber
            };

            _channel.Writer.TryWrite(log);
        }

        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await DrainAndWriteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }
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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            await _backgroundTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        await _flushLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await DrainAndWriteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }

        _cts.Dispose();
        _flushLock.Dispose();
    }

    private async Task BackgroundFlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, ct).ConfigureAwait(false);
                await _flushLock.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await DrainAndWriteAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Deploy] Background log flush failed");
            }
            finally
            {
                _flushLock.Release();
            }
        }
    }

    private async Task DrainAndWriteAsync(CancellationToken ct)
    {
        var batch = new List<ServerTaskLog>();

        while (_channel.Reader.TryRead(out var log))
            batch.Add(log);

        if (batch.Count == 0) return;

        await using var db = CreateContext();
        await db.AddRangeAsync(batch, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private SquidDbContext CreateContext() => new(_dbOptions);
}
