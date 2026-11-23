using Npgsql;
using Squid.Core.Halibut.DataStreamStorage;

namespace Squid.Core.Halibut.RedisQueue;

public interface IReplicaStore
{
    /// <summary>Records this replica as alive now and returns how many OTHER replicas were seen within <paramref name="liveWindow"/>, by the database's own clock (replicas' clocks may differ).</summary>
    Task<int> BeatAsync(Guid replicaId, TimeSpan liveWindow, CancellationToken ct);

    /// <summary>Removes this replica's row, so a clean shutdown is noticed by the others at once instead of after the live window.</summary>
    Task LeaveAsync(Guid replicaId, CancellationToken ct);
}

/// <summary>
/// <see cref="IReplicaStore"/> on the <c>server_replica</c> table, in the shared database: the one thing every replica already depends on, so replica discovery adds no
/// dependency (a Redis registry would put Redis into the path of a single-replica install, which is exactly what must stay out of it).
/// </summary>
public sealed class PostgresReplicaStore(ILifetimeScope scope) : IReplicaStore, ISingletonDependency
{
    private const string BeatSql = """
        WITH beat AS (INSERT INTO server_replica (replica_id, last_seen) VALUES ($1, now()) ON CONFLICT (replica_id) DO UPDATE SET last_seen = now() RETURNING 1),
             prune AS (DELETE FROM server_replica WHERE last_seen < now() - interval '1 day' AND replica_id <> $1 RETURNING 1)
        SELECT count(*) FROM server_replica WHERE replica_id <> $1 AND last_seen > now() - make_interval(secs => $2)
        """;

    private const string LeaveSql = "DELETE FROM server_replica WHERE replica_id = $1";

    private readonly Lazy<string> _connectionString = new(() => PostgresDataStreamPersistence.ReadConnectionString(scope));

    public async Task<int> BeatAsync(Guid replicaId, TimeSpan liveWindow, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = PostgresDataStreamPersistence.Command(connection, BeatSql, replicaId, liveWindow.TotalSeconds);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task LeaveAsync(Guid replicaId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = PostgresDataStreamPersistence.Command(connection, LeaveSql, replicaId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString.Value);

        await connection.OpenAsync(ct).ConfigureAwait(false);

        return connection;
    }
}

/// <summary>
/// Beats on an interval and publishes whether another replica is alive. Not being able to tell (the database does not answer) counts as no other replica: the queue then
/// stays local, which is what a request for an agent attached here needs anyway, and only a request for an agent attached elsewhere waits for the polling-queue timeout.
/// </summary>
public sealed class ReplicaHeartbeat(IReplicaStore store, ReplicaPresence presence, RedisQueueOptions options) : IStartable, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Guid _id = Guid.NewGuid();
    private Task _loop;
    private bool _failing;
    private int _disposed;

    public void Start() => _loop ??= Task.Run(RunAsync);

    internal async Task BeatOnceAsync(CancellationToken ct)
    {
        try
        {
            presence.Set(await store.BeatAsync(_id, options.ReplicaLiveWindow, ct).ConfigureAwait(false) > 0);

            if (_failing) Log.Information("Replica heartbeat recovered");

            _failing = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            presence.Set(false);

            if (!_failing) Log.Warning(ex, "Replica heartbeat failed; requests are queued on the pod that receives them until it recovers");

            _failing = true;
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(options.ReplicaHeartbeat);

        try
        {
            do await BeatOnceAsync(_stop.Token).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Shutting down: whatever a beat in flight ran into (a cancelled call, a container already disposing) is not worth failing the shutdown for.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _stop.CancelAsync().ConfigureAwait(false);

        if (_loop != null) await _loop.ConfigureAwait(false);

        await LeaveQuietlyAsync().ConfigureAwait(false);

        _stop.Dispose();
    }

    private async Task LeaveQuietlyAsync()
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await store.LeaveAsync(_id, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not remove this replica's heartbeat row; the other replicas will stop counting it after the live window");
        }
    }
}
