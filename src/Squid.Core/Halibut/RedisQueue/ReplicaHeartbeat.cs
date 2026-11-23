using Npgsql;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.RedisQueue.Encryption;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>What one beat learned: how many OTHER replicas are alive, and the key check each of them published (by replica, for the log).</summary>
public sealed record ReplicaBeat(int OthersAlive, IReadOnlyDictionary<Guid, string> OtherKeyChecks);

public interface IReplicaStore
{
    /// <summary>
    /// Records this replica as alive now (and its <paramref name="keyCheck"/>, when it has one) and reports the OTHER replicas seen within <paramref name="liveWindow"/>,
    /// by the database's own clock (replicas' clocks may differ).
    /// </summary>
    Task<ReplicaBeat> BeatAsync(Guid replicaId, TimeSpan liveWindow, string keyCheck, CancellationToken ct);

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
        WITH beat AS (INSERT INTO server_replica (replica_id, last_seen, key_check) VALUES ($1, now(), $3) ON CONFLICT (replica_id) DO UPDATE SET last_seen = now(), key_check = COALESCE($3, server_replica.key_check) RETURNING 1),
             prune AS (DELETE FROM server_replica WHERE last_seen < now() - interval '1 day' AND replica_id <> $1 RETURNING 1)
        SELECT replica_id, key_check FROM server_replica WHERE replica_id <> $1 AND last_seen > now() - make_interval(secs => $2)
        """;

    private const string LeaveSql = "DELETE FROM server_replica WHERE replica_id = $1";

    private readonly Lazy<string> _connectionString = new(() => PostgresDataStreamPersistence.ReadConnectionString(scope));

    public async Task<ReplicaBeat> BeatAsync(Guid replicaId, TimeSpan liveWindow, string keyCheck, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = PostgresDataStreamPersistence.Command(connection, BeatSql, replicaId, liveWindow.TotalSeconds, (object)keyCheck ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var checks = new Dictionary<Guid, string>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            checks[reader.GetGuid(0)] = await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1);

        return new ReplicaBeat(checks.Count, checks);
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
/// Proves, between replicas, that they hold the same master key. Each publishes a key envelope sealed with its own (<see cref="Create"/>); a replica that cannot open
/// another's (<see cref="CanOpen"/>) has a different master key, so what either seals into Redis the other drops as unreadable.
/// </summary>
public interface IQueueKeyCheck
{
    /// <summary>A sealed value to publish, or null when no master key is available (the startup check already says so).</summary>
    string Create();

    bool CanOpen(string keyCheck);
}

public sealed class QueueKeyCheck(IQueueMessageKeyProtector protector) : IQueueKeyCheck
{
    public string Create()
    {
        try
        {
            return protector.Wrap(new byte[QueueMessageEnvelope.KeyBytes]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool CanOpen(string keyCheck)
    {
        try
        {
            protector.Unwrap(keyCheck);

            return true;
        }
        catch (QueueMessageUnreadableException)
        {
            return false;
        }
    }
}

/// <summary>
/// Beats on an interval and publishes whether another replica is alive. Not being able to tell (the database does not answer) counts as no other replica: the queue then
/// stays local, which is what a request for an agent attached here needs anyway, and only a request for an agent attached elsewhere waits for the polling-queue timeout.
/// It also checks that the replicas it sees hold the same master key as this one, and says so, loudly, when one does not.
/// </summary>
public sealed class ReplicaHeartbeat(IReplicaStore store, ReplicaPresence presence, RedisQueueOptions options, IQueueKeyCheck keyCheck = null) : IStartable, IAsyncDisposable
{
    private const int MaxRememberedChecks = 64;

    private readonly CancellationTokenSource _stop = new();
    private readonly Guid _id = Guid.NewGuid();
    private readonly Dictionary<string, bool> _checked = new();
    private Task _loop;
    private bool _failing;
    private int _disposed;
    private bool _keyCheckCreated;
    private string _keyCheck;

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>The replicas found to hold another master key, each reported once.</summary>
    internal HashSet<Guid> ReplicasWithAnotherMasterKey { get; } = new();

    internal async Task BeatOnceAsync(CancellationToken ct)
    {
        try
        {
            var beat = await store.BeatAsync(_id, options.ReplicaLiveWindow, OwnKeyCheck(), ct).ConfigureAwait(false);

            presence.Set(beat.OthersAlive > 0);
            VerifyMasterKeys(beat);

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

    // Sealing it is a PBKDF2, so it is made once, here in the background, and published with every beat after.
    private string OwnKeyCheck()
    {
        if (_keyCheckCreated || keyCheck == null) return _keyCheck;

        _keyCheck = keyCheck.Create();
        _keyCheckCreated = _keyCheck != null;

        return _keyCheck;
    }

    private void VerifyMasterKeys(ReplicaBeat beat)
    {
        if (keyCheck == null) return;

        foreach (var (replica, other) in beat.OtherKeyChecks)
            if (!string.IsNullOrEmpty(other) && IsNewlyFoundToMismatch(other))
                ReportMismatch(replica);
    }

    private void ReportMismatch(Guid replica)
    {
        ReplicasWithAnotherMasterKey.Add(replica);

        Log.Error("Server replica {Replica} seals its queue keys with a different master key than this replica (Security:VariableEncryption:MasterKey). Requests that cross between them through Redis are dropped as unreadable until every replica uses the same master key", replica);
    }

    // A replica's value is constant for its lifetime and opening it is a PBKDF2, so each value is judged once, and a mismatch is reported once, not on every beat.
    private bool IsNewlyFoundToMismatch(string other)
    {
        if (_checked.ContainsKey(other)) return false;

        if (_checked.Count >= MaxRememberedChecks) _checked.Clear();

        _checked[other] = keyCheck.CanOpen(other);

        return !_checked[other];
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
