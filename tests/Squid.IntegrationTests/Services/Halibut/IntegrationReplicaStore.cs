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

    [Fact]
    public async Task AnotherReplicasBeat_IsCounted_UntilItLeaves()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var baseline = await Store.BeatAsync(me, Window, CancellationToken.None);

        await Store.BeatAsync(other, Window, CancellationToken.None);

        (await Store.BeatAsync(me, Window, CancellationToken.None)).ShouldBe(baseline + 1, "the other replica is counted, this one is not");

        await Store.LeaveAsync(other, CancellationToken.None);

        (await Store.BeatAsync(me, Window, CancellationToken.None)).ShouldBe(baseline, "a replica that left cleanly is noticed at once");

        await Store.LeaveAsync(me, CancellationToken.None);
    }

    [Fact]
    public async Task AReplicaThatStoppedBeating_IsNotCountedOnceItsLastBeatIsOlderThanTheWindow()
    {
        var me = Guid.NewGuid();
        var silent = Guid.NewGuid();
        var baseline = await Store.BeatAsync(me, Window, CancellationToken.None);

        await Store.BeatAsync(silent, Window, CancellationToken.None);
        await SetLastSeenAsync(silent, "1 minute");

        (await Store.BeatAsync(me, Window, CancellationToken.None)).ShouldBe(baseline, "a crashed replica stops counting after the live window");
        (await Store.BeatAsync(me, TimeSpan.FromMinutes(5), CancellationToken.None)).ShouldBe(baseline + 1, "the window decides, by the database's clock");

        await Store.LeaveAsync(silent, CancellationToken.None);
        await Store.LeaveAsync(me, CancellationToken.None);
    }

    [Fact]
    public async Task ABeat_PrunesRowsOlderThanADay_ButNeverItsOwn()
    {
        var me = Guid.NewGuid();
        var ancient = Guid.NewGuid();

        await Store.BeatAsync(ancient, Window, CancellationToken.None);
        await SetLastSeenAsync(ancient, "2 days");
        await Store.BeatAsync(me, Window, CancellationToken.None);

        (await CountAsync(ancient)).ShouldBe(0L, "a replica that never came back is pruned");
        (await CountAsync(me)).ShouldBe(1L);

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
