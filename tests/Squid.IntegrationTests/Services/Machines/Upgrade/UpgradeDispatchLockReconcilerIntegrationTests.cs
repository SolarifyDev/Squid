using System.Threading;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Enums.Caching;
using StackExchange.Redis;

namespace Squid.IntegrationTests.Services.Machines.Upgrade;

/// <summary>
/// Real-Redis regression guard for <see cref="UpgradeDispatchLockReconciler"/>.
///
/// <para>The existing pure-logic unit suite
/// (<c>UpgradeDispatchLockReconcilerTests.cs</c>, ~308 lines) exhaustively
/// covers the staleness-decision predicate (<c>ShouldClearLockForStatus</c>)
/// but has ZERO coverage of the actual Redis <c>KeyDeleteAsync</c> call.
/// A refactor that stubs the delete path to a no-op — or rewires it to the
/// wrong server, wrong key format, or wrong multiplexer — passes every
/// existing predicate test while silently breaking operator-facing
/// "next-upgrade-click" recovery after a stale lock.</para>
///
/// <para>Observable prod failure without this test: operator clicks Upgrade,
/// agent crashes mid-flight, Redis lock stays held forever (no stale-clear),
/// every subsequent click returns
/// <c>"Machine is currently being upgraded by another request"</c> even
/// though nothing is actually upgrading. Only remedy is SSH to Redis and
/// manually <c>DEL</c> the key — a support burden that the reconciler was
/// SPECIFICALLY designed to eliminate.</para>
///
/// <para>Setup: connects to Redis at localhost:6379. If unreachable, the
/// test fails fast (infrastructure prerequisite, same as Postgres for
/// other integration tests). Isolation: uses a machineId number
/// (<c>99042</c>) deliberately outside any realistic range used by other
/// tests, so no cross-test interference with real machine records.</para>
/// </summary>
public sealed class UpgradeDispatchLockReconcilerIntegrationTests
{
    // Matches src/Squid.Api/appsettings.json RedisCacheConnectionString. If prod
    // Redis moves off localhost, update here — integration tests assume a local
    // Redis dev container (same convention as the Postgres connection string in
    // appsettings.json for this project).
    private const string RedisConnectionString =
        "127.0.0.1:6379,password=,ssl=False,abortConnect=False,syncTimeout=5000,allowAdmin=true";

    // An ID far outside any range used by real machines or other integration
    // tests — prevents accidental collision if this test and another
    // happen to both seed keys at the same time.
    private const int SentinelMachineId = 99042;

    // The EXACT key format UpgradeDispatchLockReconciler.BuildLockKey produces
    // for SentinelMachineId. Hardcoded here rather than calling BuildLockKey —
    // the format is `internal` to Squid.Core and that's intentional. Drift
    // between this literal and the production format is guarded separately by
    // LockKey_MatchesMachineUpgradeServiceFormat in the unit suite.
    private const string SentinelLockKey = "squid:upgrade:machine:99042";

    [Fact]
    public async Task ClearLockIfStaleAsync_StaleInProgressStatus_ActuallyDeletesRedisKey()
    {
        // Primary regression guard. Verifies the reconciler's ExecuteAsync
        // lambda genuinely invokes KeyDeleteAsync against the computed key
        // on a real Redis. A future refactor that turns this into a no-op
        // will fail here regardless of what the predicate tests say.

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString).ConfigureAwait(false);
        var db = multiplexer.GetDatabase();

        // Clean start — defensive in case a prior test run left the sentinel key around.
        await db.KeyDeleteAsync(SentinelLockKey).ConfigureAwait(false);

        // Seed: pretend an earlier dispatch acquired the lock.
        await db.StringSetAsync(SentinelLockKey, "sentinel-from-a-crashed-dispatch", TimeSpan.FromMinutes(30)).ConfigureAwait(false);

        (await db.KeyExistsAsync(SentinelLockKey).ConfigureAwait(false)).ShouldBeTrue(
            "test setup: seeded sentinel key must exist before the reconciler runs " +
            "(otherwise the post-call absence-check proves nothing)");

        var runner = new PassthroughRedisSafeRunner(multiplexer);
        var reconciler = new UpgradeDispatchLockReconciler(() => runner);

        var staleStatus = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "IN_PROGRESS",
            // 15 minutes past the 10-minute StalenessThreshold — unambiguously stale.
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            TargetVersion = "1.5.8",
            InstallMethod = "apt",
            Detail = "Selecting upgrade method"
        };

        await reconciler.ClearLockIfStaleAsync(SentinelMachineId, staleStatus, CancellationToken.None).ConfigureAwait(false);

        var stillExistsAfter = await db.KeyExistsAsync(SentinelLockKey).ConfigureAwait(false);
        stillExistsAfter.ShouldBeFalse(
            customMessage:
                $"reconciler.ClearLockIfStaleAsync must actually DELETE the Redis key " +
                $"'{SentinelLockKey}' when the status is a stale v2+ IN_PROGRESS. The key " +
                "still exists after the call — reconciler's real-Redis path is broken " +
                "(the delete was stubbed, rewired to a different key, or points at a " +
                "different multiplexer). Downstream operator impact: after a crashed " +
                "dispatch the stale lock stays held FOREVER, every subsequent UI Upgrade " +
                "click returns 'another request is upgrading' and the only recovery is " +
                "manual DEL via redis-cli — exactly the pain the reconciler was " +
                "introduced to eliminate.");
    }

    [Fact]
    public async Task ClearLockIfStaleAsync_FreshInProgressStatus_PreservesRedisKey()
    {
        // Companion guarding the OTHER direction of the predicate: a fresh
        // IN_PROGRESS status (within the 10-min threshold) means a dispatch
        // is legitimately in flight. Reconciler MUST NOT touch the lock —
        // doing so would let a second operator click race the first
        // dispatch's systemd-restart sequence.
        //
        // Without this companion, a refactor like "ClearLockIfStaleAsync
        // just deletes unconditionally — the predicate is evaluated at the
        // caller" passes the primary test above but starts silently
        // clearing LIVE locks on every health check tick, causing
        // concurrent-dispatch bugs that are near-impossible to reproduce.

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString).ConfigureAwait(false);
        var db = multiplexer.GetDatabase();

        await db.KeyDeleteAsync(SentinelLockKey).ConfigureAwait(false);
        await db.StringSetAsync(SentinelLockKey, "live-dispatch-in-progress", TimeSpan.FromMinutes(30)).ConfigureAwait(false);

        var runner = new PassthroughRedisSafeRunner(multiplexer);
        var reconciler = new UpgradeDispatchLockReconciler(() => runner);

        var freshStatus = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "IN_PROGRESS",
            // 2 min ago — well within the 10-min StalenessThreshold. Not stale.
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            TargetVersion = "1.5.8",
            InstallMethod = "apt",
            Detail = "Running apt install"
        };

        await reconciler.ClearLockIfStaleAsync(SentinelMachineId, freshStatus, CancellationToken.None).ConfigureAwait(false);

        var stillExistsAfter = await db.KeyExistsAsync(SentinelLockKey).ConfigureAwait(false);
        stillExistsAfter.ShouldBeTrue(
            customMessage:
                $"reconciler.ClearLockIfStaleAsync must NOT delete the Redis key " +
                $"'{SentinelLockKey}' when the status is fresh IN_PROGRESS (within 10 " +
                "min). Key is gone — reconciler is deleting unconditionally, which " +
                "races live dispatches with mid-restart lock release and allows a " +
                "second operator click to dispatch on top of a still-running first " +
                "upgrade. Concurrent dpkg / systemctl-restart collision.");

        // Cleanup
        await db.KeyDeleteAsync(SentinelLockKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Minimal <see cref="IRedisSafeRunner"/> that just invokes the supplied
    /// func against the single shared multiplexer — NO exception swallowing,
    /// NO server-keyed routing. The production <see cref="RedisSafeRunner"/>
    /// silently logs-and-swallows any Redis exception, which is correct for
    /// the service but hides test failures. We want failures to surface.
    /// </summary>
    private sealed class PassthroughRedisSafeRunner : IRedisSafeRunner
    {
        private readonly ConnectionMultiplexer _multiplexer;

        public PassthroughRedisSafeRunner(ConnectionMultiplexer multiplexer)
        {
            _multiplexer = multiplexer;
        }

        public Task ExecuteAsync(Func<ConnectionMultiplexer, Task> func, RedisServer server = RedisServer.System)
            => func(_multiplexer);

        public Task<T> ExecuteAsync<T>(Func<ConnectionMultiplexer, Task<T>> func, RedisServer server = RedisServer.System) where T : class
            => throw new NotImplementedException("reconciler doesn't call the generic overload");

        public Task<List<T>> ExecuteAsync<T>(Func<ConnectionMultiplexer, Task<List<T>>> func, RedisServer server = RedisServer.System) where T : class
            => throw new NotImplementedException("reconciler doesn't call the list overload");

        public Task ExecuteWithLockAsync(string lockKey, Func<Task> logic, TimeSpan? expiry = null, TimeSpan? wait = null, TimeSpan? retry = null, RedisServer server = RedisServer.System)
            => throw new NotImplementedException("reconciler doesn't call ExecuteWithLockAsync");

        public Task<T> ExecuteWithLockAsync<T>(string lockKey, Func<Task<T>> logic, TimeSpan? expiry = null, TimeSpan? wait = null, TimeSpan? retry = null, RedisServer server = RedisServer.System) where T : class
            => throw new NotImplementedException("reconciler doesn't call ExecuteWithLockAsync<T>");
    }
}
