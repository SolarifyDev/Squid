using System.Collections.Concurrent;
using System.Threading;
using Moq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums;
using Squid.Message.Enums.Caching;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Machines;
using StackExchange.Redis;

namespace Squid.IntegrationTests.Services.Machines;

/// <summary>
/// Fidelity tier: HIGH. Real Postgres + the real <see cref="RedisSafeRunner"/> (RedLock) against Redis at 127.0.0.1:6379 +
/// the real <see cref="MachineHealthCheckService"/> and <see cref="MachineHealthCheckCoordinator"/> (per-machine DI scopes, real
/// DbContexts). Only the transport strategy is substituted so probes can be gated / hung deterministically. "Two replicas" are
/// simulated with separate in-process lock instances, so ONLY the Redis lock can serialise them.
/// </summary>
public class MachineHealthSweepIntegrationTests : TestBase
{
    private static readonly string RedisConnectionString =
        System.Environment.GetEnvironmentVariable("SQUID_TEST_REDIS_CONN_STRING")
        ?? "127.0.0.1:6379,password=,ssl=False,abortConnect=False,syncTimeout=5000";

    private static readonly Lazy<ConnectionMultiplexer> Redis = new(() => ConnectionMultiplexer.Connect(RedisConnectionString));

    public MachineHealthSweepIntegrationTests() : base("MachineHealthSweep", "squid_it_health_sweep")
    {
    }

    [Fact]
    public async Task TwoReplicasSweepingConcurrently_OnlyOneProbes_AndLockIsReleasedAfterwards()
    {
        var ids = await SeedMachinesAsync(3).ConfigureAwait(false);
        var strategy = new FakeStrategy();
        var gate = new TaskCompletionSource();
        strategy.Behavior = async (_, _) => { await gate.Task.ConfigureAwait(false); return new HealthCheckResult(true, "ok"); };

        var replicaA = RunSweepAsync(strategy, new HealthCheckInProcessLocks(), probeTimeoutSeconds: 60);
        await WaitUntilAsync(() => strategy.TotalProbes == 3).ConfigureAwait(false);

        await RunSweepAsync(strategy, new HealthCheckInProcessLocks(), probeTimeoutSeconds: 60).ConfigureAwait(false);

        strategy.TotalProbes.ShouldBe(3, "the second replica's sweep must exit without probing while the first holds the Redis sweep lock");

        gate.SetResult();
        await replicaA.ConfigureAwait(false);

        ids.ShouldAllBe(id => strategy.ProbesFor(id) == 1);
        (await LoadStatusesAsync(ids).ConfigureAwait(false)).ShouldAllBe(s => s == MachineHealthStatus.Healthy);

        strategy.Behavior = (_, _) => Task.FromResult(new HealthCheckResult(true, "ok"));
        var late = (await SeedMachinesAsync(1).ConfigureAwait(false)).Single();
        await RunSweepAsync(strategy, new HealthCheckInProcessLocks(), probeTimeoutSeconds: 60).ConfigureAwait(false);

        strategy.ProbesFor(late).ShouldBe(1, "the Redis sweep lock must be released when the sweep finishes");
    }

    [Fact]
    public async Task HangingStrategyForOneMachine_DoesNotPreventOthersFromBeingUpdated()
    {
        var ids = await SeedMachinesAsync(5).ConfigureAwait(false);
        var hanging = ids[0];
        var strategy = new FakeStrategy();
        strategy.Behavior = async (id, _) =>
        {
            if (id == hanging) await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            return new HealthCheckResult(true, "ok");
        };
        var started = DateTime.UtcNow;

        await RunSweepAsync(strategy, new HealthCheckInProcessLocks(), probeTimeoutSeconds: 2, maxParallelism: 2).ConfigureAwait(false);

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(30), "the sweep is bounded by the probe timeout, not by the hung agent");
        var statuses = await LoadStatusesAsync(ids).ConfigureAwait(false);
        statuses[0].ShouldBe(MachineHealthStatus.Unavailable);
        statuses.Skip(1).ShouldAllBe(s => s == MachineHealthStatus.Healthy);
        (await LoadDetailAsync(hanging).ConfigureAwait(false)).ShouldContain("timed out after");
    }

    [Fact]
    public async Task ManualCheck_WhileSweepIsRunning_IsNotBlocked()
    {
        var ids = await SeedMachinesAsync(2).ConfigureAwait(false);
        var strategy = new FakeStrategy();
        var gate = new TaskCompletionSource();
        strategy.Behavior = async (id, _) =>
        {
            if (id == ids[0]) await gate.Task.ConfigureAwait(false);

            return new HealthCheckResult(true, "ok");
        };
        await SetNeverCheckedAsync(ids[1], due: false).ConfigureAwait(false);

        var sweep = RunSweepAsync(strategy, new HealthCheckInProcessLocks(), probeTimeoutSeconds: 60);
        await WaitUntilAsync(() => strategy.ProbesFor(ids[0]) == 1).ConfigureAwait(false);

        var manual = await RunWithOverridesAsync(strategy, new HealthCheckInProcessLocks(), 60, 4, svc => svc.ManualHealthCheckAsync(ids[1])).ConfigureAwait(false);

        manual.Successful.ShouldBeTrue("a manual check of another machine must not wait for the running sweep");

        gate.SetResult();
        await sweep.ConfigureAwait(false);
    }

    [Fact]
    public async Task UnconfiguredTimeout_HungProbe_IsPersistedUnavailable_BeforeTheSweepDeadline()
    {
        var ids = await SeedMachinesAsync(1).ConfigureAwait(false);
        var strategy = new FakeStrategy();
        strategy.Behavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); return new HealthCheckResult(true, "unreachable"); };

        await RunSweepAsync(strategy, new HealthCheckInProcessLocks(), probeTimeoutSeconds: 0, sweepDeadlineSeconds: 10).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        (await LoadStatusesAsync(ids).ConfigureAwait(false)).ShouldAllBe(s => s == MachineHealthStatus.Unavailable);
        (await LoadDetailAsync(ids[0]).ConfigureAwait(false)).ShouldContain("timed out");
    }

    [Fact]
    public async Task Container_RegistersHealthCheckSetting_WithDefaults()
    {
        var setting = await Run<MachineHealthCheckSetting, MachineHealthCheckSetting>(s => Task.FromResult(s)).ConfigureAwait(false);

        setting.ProbeTimeoutSeconds.ShouldBe(75);
        setting.MaxParallelism.ShouldBe(6);
        setting.SweepDeadlineSeconds.ShouldBe(300);
    }

    // ── helpers ──

    private Task RunSweepAsync(FakeStrategy strategy, HealthCheckInProcessLocks locks, int probeTimeoutSeconds, int maxParallelism = 4, int sweepDeadlineSeconds = 300)
        => RunWithOverridesAsync<object>(strategy, locks, probeTimeoutSeconds, maxParallelism, async svc => { await svc.AutoHealthCheckForAllAsync().ConfigureAwait(false); return null; }, sweepDeadlineSeconds);

    private async Task<T> RunWithOverridesAsync<T>(FakeStrategy strategy, HealthCheckInProcessLocks locks, int probeTimeoutSeconds, int maxParallelism, Func<IMachineHealthCheckService, Task<T>> action, int sweepDeadlineSeconds = 300)
    {
        var options = new MachineHealthCheckSetting { ProbeTimeoutSeconds = probeTimeoutSeconds, MaxParallelism = maxParallelism, SweepDeadlineSeconds = sweepDeadlineSeconds };
        var transport = new Mock<IDeploymentTransport>();
        transport.Setup(t => t.HealthChecker).Returns(strategy);
        transport.Setup(t => t.CommunicationStyle).Returns(CommunicationStyle.KubernetesApi);
        var registry = new Mock<ITransportRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(transport.Object);

        return await Run<IMachineHealthCheckService, T>(action, builder =>
        {
            builder.RegisterInstance(Redis.Value).Keyed<ConnectionMultiplexer>(RedisServer.System).ExternallyOwned();
            builder.RegisterInstance(Redis.Value).Keyed<ConnectionMultiplexer>(RedisServer.Vector).ExternallyOwned();
            builder.RegisterInstance(registry.Object).As<ITransportRegistry>();
            builder.Register(c => new MachineHealthCheckCoordinator(c.Resolve<IRedisSafeRunner>(), c.Resolve<ILifetimeScope>(), locks, options)).As<IMachineHealthCheckCoordinator>().InstancePerLifetimeScope();
        }).ConfigureAwait(false);
    }

    private async Task<List<int>> SeedMachinesAsync(int count)
    {
        var ids = new List<int>();

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            for (var i = 0; i < count; i++)
            {
                var name = $"sweep-{Guid.NewGuid():N}";
                var machine = new Machine { Name = name, Slug = name, IsDisabled = false, Roles = "[]", EnvironmentIds = "[]", Endpoint = "{\"CommunicationStyle\":\"KubernetesApi\"}", SpaceId = 1, HealthStatus = MachineHealthStatus.Unknown };
                await repo.InsertAsync(machine, CancellationToken.None).ConfigureAwait(false);
                await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                ids.Add(machine.Id);
            }
        }).ConfigureAwait(false);

        return ids;
    }

    private Task SetNeverCheckedAsync(int machineId, bool due)
        => Run<IMachineDataProvider>(async p =>
        {
            var machine = await p.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);
            machine.HealthLastChecked = due ? null : DateTimeOffset.UtcNow;
            await p.UpdateMachineAsync(machine, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        });

    private async Task<List<MachineHealthStatus>> LoadStatusesAsync(List<int> ids)
    {
        var statuses = new List<MachineHealthStatus>();

        foreach (var id in ids)
            statuses.Add((await Run<IMachineDataProvider, Machine>(p => p.GetMachinesByIdAsync(id, CancellationToken.None)).ConfigureAwait(false)).HealthStatus);

        return statuses;
    }

    private async Task<string> LoadDetailAsync(int id)
        => (await Run<IMachineDataProvider, Machine>(p => p.GetMachinesByIdAsync(id, CancellationToken.None)).ConfigureAwait(false)).HealthDetail;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("probe condition not reached within 20s");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private sealed class FakeStrategy : IHealthCheckStrategy
    {
        private readonly ConcurrentDictionary<int, int> _probes = new();

        public Func<int, CancellationToken, Task<HealthCheckResult>> Behavior { get; set; }

        public int TotalProbes => _probes.Values.Sum();

        public int ProbesFor(int machineId) => _probes.TryGetValue(machineId, out var n) ? n : 0;

        public Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct, MachineHealthCheckPolicyDto healthCheckPolicy = null)
        {
            _probes.AddOrUpdate(machine.Id, 1, (_, n) => n + 1);

            return Behavior(machine.Id, ct);
        }
    }
}
