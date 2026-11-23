using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Persistence.Db;

namespace Squid.IntegrationTests.Services.HalibutReplicas;

/// <summary>
/// Integration tier on the real Postgres and the real migration: the replica table the heartbeat writes and the "is another replica alive" count the Redis routing
/// is gated on. The container's own heartbeat may be a replica too, so every assertion is relative to a baseline taken from the same call.
/// </summary>
public class IntegrationReplicaStore : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    public IntegrationReplicaStore() : base("HalibutReplicaStore", "squid_it_halibut_replica_store")
    {
    }

    private IReplicaStore Store => CurrentScope.Resolve<IReplicaStore>();

    private bool _heartbeatStopped;

    // The container runs its own heartbeat, a replica that appears at any moment and makes every count move under the test. Stopping it (which also removes its row)
    // leaves the table to the test, so the counts are exact.
    private async Task StopContainerHeartbeatAsync()
    {
        if (_heartbeatStopped) return;

        _heartbeatStopped = true;

        await CurrentScope.Resolve<ReplicaHeartbeat>().DisposeAsync();
    }

    private async Task<int> OthersAsync(Guid me, TimeSpan? window = null)
    {
        await StopContainerHeartbeatAsync();

        return (await Store.BeatAsync(me, window ?? Window, null, CancellationToken.None)).OthersAlive;
    }

    [Fact]
    public async Task AnotherReplicasBeat_IsCounted_UntilItLeaves()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var baseline = await OthersAsync(me);

        await OthersAsync(other);

        (await OthersAsync(me)).ShouldBe(baseline + 1, "the other replica is counted, this one is not");

        await Store.LeaveAsync(other, CancellationToken.None);

        (await OthersAsync(me)).ShouldBe(baseline, "a replica that left cleanly is noticed at once");

        await Store.LeaveAsync(me, CancellationToken.None);
    }

    [Fact]
    public async Task AReplicaThatStoppedBeating_IsNotCountedOnceItsLastBeatIsOlderThanTheWindow()
    {
        var me = Guid.NewGuid();
        var silent = Guid.NewGuid();
        var baseline = await OthersAsync(me);

        await OthersAsync(silent);
        await SetLastSeenAsync(silent, "1 minute");

        (await OthersAsync(me)).ShouldBe(baseline, "a crashed replica stops counting after the live window");
        (await OthersAsync(me, TimeSpan.FromMinutes(5))).ShouldBe(baseline + 1, "the window decides, by the database's clock");

        await Store.LeaveAsync(silent, CancellationToken.None);
        await Store.LeaveAsync(me, CancellationToken.None);
    }

    [Fact]
    public async Task ABeat_PrunesRowsOlderThanADay_ButNeverItsOwn()
    {
        var me = Guid.NewGuid();
        var ancient = Guid.NewGuid();

        await OthersAsync(ancient);
        await SetLastSeenAsync(ancient, "2 days");
        await OthersAsync(me);

        (await CountAsync(ancient)).ShouldBe(0L, "a replica that never came back is pruned");
        (await CountAsync(me)).ShouldBe(1L);

        await Store.LeaveAsync(me, CancellationToken.None);
    }

    [Fact]
    public async Task TheKeyCheckAReplicaPublishes_IsSeenByTheOthers_AndKeptWhenALaterBeatHasNone()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        await Store.BeatAsync(other, Window, "sealed-check-value", CancellationToken.None);

        var seen = await Store.BeatAsync(me, Window, null, CancellationToken.None);

        seen.OtherKeyChecks[other].ShouldBe("sealed-check-value");

        await Store.BeatAsync(other, Window, null, CancellationToken.None);

        (await Store.BeatAsync(me, Window, null, CancellationToken.None)).OtherKeyChecks[other].ShouldBe("sealed-check-value", "a beat that has not computed its check yet does not erase the published one");

        await Store.LeaveAsync(other, CancellationToken.None);
        await Store.LeaveAsync(me, CancellationToken.None);
    }

    private async Task SetLastSeenAsync(Guid replicaId, string age)
    {
        await using var connection = await OpenRawAsync();
        await using var command = new NpgsqlCommand($"UPDATE server_replica SET last_seen = now() - interval '{age}' WHERE replica_id = $1", connection);

        command.Parameters.Add(new NpgsqlParameter { Value = replicaId });

        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(Guid replicaId)
    {
        await using var connection = await OpenRawAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM server_replica WHERE replica_id = $1", connection);

        command.Parameters.Add(new NpgsqlParameter { Value = replicaId });

        return (long)await command.ExecuteScalarAsync();
    }

    private async Task<NpgsqlConnection> OpenRawAsync()
    {
        string connectionString = null;

        await Run<SquidDbContext>(db =>
        {
            connectionString = db.Database.GetConnectionString();

            return Task.CompletedTask;
        });

        var connection = new NpgsqlConnection(connectionString);

        await connection.OpenAsync();

        return connection;
    }
}
