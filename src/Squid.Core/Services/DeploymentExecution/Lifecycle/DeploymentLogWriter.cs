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

    /// <summary>
    /// Phase-6.4: env var that selects the in-memory buffer capacity. Default
    /// (unset / blank / unrecognised) is <c>50_000</c> entries — caps memory
    /// under sustained DB blips while leaving plenty of headroom for normal
    /// deploys. <c>"unbounded"</c> restores the pre-Phase-6 behaviour
    /// (no cap; OOM possible under DB stall + heavy producer rate).
    /// </summary>
    public const string BufferCapacityEnvVar = "SQUID_DEPLOY_LOG_BUFFER_CAPACITY";

    private const int DefaultBufferCapacity = 50_000;

    private readonly DbContextOptions<SquidDbContext> _dbOptions;
    private readonly System.Threading.Channels.Channel<ServerTaskLog> _channel;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _backgroundTask;

    private long _droppedLogCount;
    private long _lastDropWarningAtCount;

    /// <summary>
    /// Total ServerTaskLog entries the producer attempted to write but the
    /// bounded channel rejected (capacity hit). Operator-visible via
    /// /metrics or test assertions; resets only on process restart.
    /// </summary>
    public long DroppedLogCount => Interlocked.Read(ref _droppedLogCount);

    /// <summary>
    /// Production constructor — reads capacity from
    /// <see cref="BufferCapacityEnvVar"/>, falls back to 50_000.
    /// </summary>
    public DeploymentLogWriter(DbContextOptions<SquidDbContext> dbOptions)
        : this(dbOptions, ParseBufferCapacity(System.Environment.GetEnvironmentVariable(BufferCapacityEnvVar)))
    {
    }

    /// <summary>
    /// Test seam: explicit capacity (or null for unbounded). Production
    /// callers should use the single-arg ctor which threads through the env
    /// var.
    /// </summary>
    internal DeploymentLogWriter(DbContextOptions<SquidDbContext> dbOptions, int? capacity)
    {
        _dbOptions = dbOptions;
        _channel = capacity.HasValue
            ? System.Threading.Channels.Channel.CreateBounded<ServerTaskLog>(
                new System.Threading.Channels.BoundedChannelOptions(capacity.Value)
                {
                    SingleWriter = false,
                    SingleReader = false,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                })
            : System.Threading.Channels.Channel.CreateUnbounded<ServerTaskLog>(
                new System.Threading.Channels.UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
        _backgroundTask = BackgroundFlushLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Pure parser exposed for unit testing. Returns a positive int for a
    /// valid capacity, or null for unbounded mode.
    /// </summary>
    internal static int? ParseBufferCapacity(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultBufferCapacity;

        var trimmed = raw.Trim();

        if (string.Equals(trimmed, "unbounded", StringComparison.OrdinalIgnoreCase))
            return null;

        if (int.TryParse(trimmed, out var explicitCap) && explicitCap > 0)
            return explicitCap;

        return DefaultBufferCapacity;
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

        if (!_channel.Writer.TryWrite(log))
            OnBufferFull();

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

            if (!_channel.Writer.TryWrite(log))
                OnBufferFull();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Bounded channel rejected the write. Increment the counter and, when
    /// drops cross a 1024-entry threshold since the last warning, emit a
    /// structured Serilog warning so operators see sustained pressure
    /// without log-spamming on every single drop.
    /// </summary>
    private void OnBufferFull()
    {
        var dropped = Interlocked.Increment(ref _droppedLogCount);

        // Throttle warnings: emit once per 1024 drops.
        var lastSeen = Interlocked.Read(ref _lastDropWarningAtCount);
        if (dropped - lastSeen < 1024) return;

        // Race-tolerant: if multiple threads cross the threshold simultaneously
        // the CAS guarantees only one wins and emits the warning.
        if (Interlocked.CompareExchange(ref _lastDropWarningAtCount, dropped, lastSeen) != lastSeen)
            return;

        Serilog.Log.Warning(
            "[Deploy] Log buffer full — {DroppedCount} entries dropped since process start. " +
            "DB writer not draining fast enough; consider raising {EnvVar} or investigating DB latency.",
            dropped, BufferCapacityEnvVar);
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
