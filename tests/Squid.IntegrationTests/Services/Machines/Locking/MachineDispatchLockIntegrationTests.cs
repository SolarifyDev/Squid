using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.Machines.Locking;
using Squid.Message.Enums.Caching;
using StackExchange.Redis;

namespace Squid.IntegrationTests.Services.Machines.Locking;

/// <summary>
/// Real-Redis end-to-end coverage of <see cref="MachineDispatchLock"/> over the production
/// <see cref="RedisSafeRunner"/> (real RedLock). Proves the cross-pod mutual exclusion the
/// deploy↔upgrade guard relies on: while one caller holds machine N's dispatch lock, a
/// concurrent caller for the same machine is rejected with
/// <see cref="MachineLockUnavailableException"/> (contention), and the key it uses is exactly
/// the one the tentacle upgrade dispatch takes — so a deployment and an upgrade for the same
/// machine cannot run at once.
///
/// <para>Connects to Redis at localhost:6379 (the CI `redis` service / local Docker), same as
/// <c>UpgradeDispatchLockReconcilerIntegrationTests</c>. Uses a sentinel machine id far outside
/// any real range to avoid collision.</para>
/// </summary>
public sealed class MachineDispatchLockIntegrationTests
{
    private static readonly string RedisConnectionString =
        Environment.GetEnvironmentVariable("SQUID_TEST_REDIS_CONN_STRING")
        ?? "127.0.0.1:6379,password=,ssl=False,abortConnect=False,syncTimeout=5000,allowAdmin=true";

    private const int SentinelMachineId = 99043;

    // Must equal UpgradeDispatchLockReconciler.BuildLockKey(SentinelMachineId). That format is
    // internal to Squid.Core (not visible here, same as UpgradeDispatchLockReconcilerIntegrationTests),
    // so it is hardcoded; the MachineDispatchLock under test computes the real key internally. The
    // unit test MachineDispatchLockTests pins deploy == upgrade key to guard drift.
    private const string SentinelLockKey = "squid:upgrade:machine:99043";

    private sealed class Probe { }

    private static (MachineDispatchLock Lock, IContainer Container) BuildRealLock()
    {
        var builder = new ContainerBuilder();
        builder.Register(_ => ConnectionMultiplexer.Connect(RedisConnectionString)).Keyed<ConnectionMultiplexer>(RedisServer.System).SingleInstance().ExternallyOwned();
        builder.Register(_ => ConnectionMultiplexer.Connect(RedisConnectionString)).Keyed<ConnectionMultiplexer>(RedisServer.Vector).SingleInstance().ExternallyOwned();
        var container = builder.Build();

        return (new MachineDispatchLock(new RedisSafeRunner(container)), container);
    }

    [Fact]
    public async Task HeldLock_BlocksConcurrentSameMachineClaim_WithContention()
    {
        var (machineLock, container) = BuildRealLock();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString).ConfigureAwait(false);
        await multiplexer.GetDatabase().KeyDeleteAsync(SentinelLockKey).ConfigureAwait(false);

        try
        {
            var acquired = new TaskCompletionSource();
            var release = new TaskCompletionSource();

            // Holder: enters the lock, signals 'acquired', then waits for 'release' while holding it.
            var holder = machineLock.RunUnderMachineLockAsync<Probe>(SentinelMachineId, async () =>
            {
                acquired.SetResult();
                await release.Task.ConfigureAwait(false);
                return new Probe();
            });

            await acquired.Task.ConfigureAwait(false); // deterministic: the lock is now held

            var contention = await Should.ThrowAsync<MachineLockUnavailableException>(() =>
                machineLock.RunUnderMachineLockAsync(SentinelMachineId, () => Task.FromResult(new Probe()))).ConfigureAwait(false);

            contention.MachineId.ShouldBe(SentinelMachineId);
            contention.IsInfrastructureFailure.ShouldBeFalse(customMessage: "the lock was held by a peer — contention, not a Redis outage");

            release.SetResult();
            (await holder.ConfigureAwait(false)).ShouldNotBeNull(customMessage: "the holder must complete and release the lock");

            // Once released, a fresh claim for the same machine succeeds.
            var afterRelease = await machineLock.RunUnderMachineLockAsync(SentinelMachineId, () => Task.FromResult(new Probe())).ConfigureAwait(false);
            afterRelease.ShouldNotBeNull();
        }
        finally
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task DifferentMachines_DoNotBlockEachOther()
    {
        var (machineLock, container) = BuildRealLock();

        try
        {
            var release = new TaskCompletionSource();
            var acquired = new TaskCompletionSource();

            var holder = machineLock.RunUnderMachineLockAsync<Probe>(SentinelMachineId, async () =>
            {
                acquired.SetResult();
                await release.Task.ConfigureAwait(false);
                return new Probe();
            });

            await acquired.Task.ConfigureAwait(false);

            // A DIFFERENT machine's lock is independent — no contention.
            var other = await machineLock.RunUnderMachineLockAsync(SentinelMachineId + 1, () => Task.FromResult(new Probe())).ConfigureAwait(false);
            other.ShouldNotBeNull();

            release.SetResult();
            await holder.ConfigureAwait(false);
        }
        finally
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
