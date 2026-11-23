using Autofac;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums.Caching;
using StackExchange.Redis;

namespace Squid.IntegrationTests.Services.Caching.Redis;

/// <summary>
/// Fidelity tier: HIGH. The real <see cref="RedisSafeRunner"/> + RedLock.net against a real Redis at 127.0.0.1:6379 (contention) and
/// against a closed port with abortConnect=False (outage) - the exact configuration production uses. Pins that an outage surfaces as
/// <see cref="LockAcquireFailedException"/> while genuine contention still returns null.
/// </summary>
public class RedisSafeRunnerLockIntegrationTests
{
    private const string RealRedis = "127.0.0.1:6379,password=,ssl=False,abortConnect=False,syncTimeout=5000";

    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task UnreachableRedis_ThrowsLockAcquireFailed_AndNeverRunsLogic()
    {
        await using var host = BuildRunner(ClosedPortConnectionString());
        var ran = false;

        var ex = await Should.ThrowAsync<LockAcquireFailedException>(() => host.Runner.ExecuteWithLockAsync<object>("squid:test:outage", () => { ran = true; return Task.FromResult(new object()); }, Expiry, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250)));

        ran.ShouldBeFalse();
        ex.LockKey.ShouldBe("squid:test:outage");
    }

    [Fact]
    public async Task UnreachableRedis_FailsFast_InsteadOfWaitingOutTheLockWait()
    {
        await using var host = BuildRunner(ClosedPortConnectionString());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<LockAcquireFailedException>(() => host.Runner.ExecuteWithLockAsync<object>("squid:test:outage-fast", () => Task.FromResult(new object()), Expiry, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500)));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "a known-down Redis must not burn the caller's whole lock wait");
    }

    [Fact]
    public async Task UnreachableRedis_WithoutWait_ThrowsLockAcquireFailed()
    {
        await using var host = BuildRunner(ClosedPortConnectionString());

        await Should.ThrowAsync<LockAcquireFailedException>(() => host.Runner.ExecuteWithLockAsync<object>("squid:test:outage-nowait", () => Task.FromResult(new object()), Expiry));
    }

    [Fact]
    public async Task HeldLock_ReturnsNull_ForContender_AndDoesNotThrow()
    {
        await using var host = BuildRunner(RealRedis);
        var key = $"squid:test:contention:{Guid.NewGuid():N}";
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var holder = host.Runner.ExecuteWithLockAsync<object>(key, async () => { holding.SetResult(); await release.Task; return new object(); }, Expiry);
        await holding.Task;

        var contender = await host.Runner.ExecuteWithLockAsync<object>(key, () => Task.FromResult(new object()), Expiry, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(200));

        contender.ShouldBeNull("another caller holds the lock: that is contention, not an outage");

        release.SetResult();
        (await holder).ShouldNotBeNull();
    }

    [Fact]
    public async Task HealthCoordinator_RedisDown_StillRunsSweepAndManualCheck_UnderInProcessLock()
    {
        await using var host = BuildRunner(ClosedPortConnectionString());
        var coordinator = new MachineHealthCheckCoordinator(host.Runner, host.Scope, new HealthCheckInProcessLocks(), new MachineHealthCheckSetting());
        var sweeps = 0;
        var checks = 0;

        var sweepRan = await coordinator.TryRunSweepAsync(_ => { sweeps++; return Task.CompletedTask; }, CancellationToken.None);
        var checkRan = await coordinator.TryRunForMachineAsync(1, TimeSpan.FromSeconds(10), () => { checks++; return Task.CompletedTask; }, CancellationToken.None);

        (sweepRan, checkRan, sweeps, checks).ShouldBe((true, true, 1, 1));
    }

    [Fact]
    public async Task FreeLock_RunsLogic_AndReturnsItsResult()
    {
        await using var host = BuildRunner(RealRedis);
        var marker = new object();

        var result = await host.Runner.ExecuteWithLockAsync<object>($"squid:test:free:{Guid.NewGuid():N}", () => Task.FromResult(marker), Expiry);

        result.ShouldBeSameAs(marker);
    }

    private static string ClosedPortConnectionString()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);

        listener.Start();

        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return $"127.0.0.1:{port},password=,ssl=False,abortConnect=False,connectTimeout=500,syncTimeout=500";
    }

    private static RunnerHost BuildRunner(string connectionString)
    {
        var multiplexer = ConnectionMultiplexer.Connect(connectionString);
        var builder = new ContainerBuilder();

        builder.RegisterInstance(multiplexer).Keyed<ConnectionMultiplexer>(RedisServer.System).ExternallyOwned();
        builder.RegisterInstance(multiplexer).Keyed<ConnectionMultiplexer>(RedisServer.Vector).ExternallyOwned();

        var container = builder.Build();

        return new RunnerHost(container, multiplexer, new RedisSafeRunner(container.Resolve<IComponentContext>()));
    }

    private sealed class RunnerHost(IContainer container, ConnectionMultiplexer multiplexer, RedisSafeRunner runner) : IAsyncDisposable
    {
        public RedisSafeRunner Runner { get; } = runner;

        public ILifetimeScope Scope => container;

        public async ValueTask DisposeAsync()
        {
            await container.DisposeAsync();
            await multiplexer.DisposeAsync();
        }
    }
}
