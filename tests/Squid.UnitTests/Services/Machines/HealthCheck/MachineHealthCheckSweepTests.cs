using System.Linq;
using Squid.Core.Handlers.CommandHandlers.Machine;
using Squid.Core.Jobs.RecurringJobs;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Enums.Caching;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Machines;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

public class MachineHealthCheckSweepTests : IDisposable
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(300);

    private readonly Mock<IMachineDataProvider> _machines = new();
    private readonly Mock<IMachinePolicyDataProvider> _policies = new();
    private readonly Mock<ITransportRegistry> _registry = new();
    private readonly Mock<IDeploymentTransport> _transport = new();
    private readonly Mock<IHealthCheckStrategy> _checker = new();
    private readonly List<HealthCheckTestHost> _hosts = new();

    public MachineHealthCheckSweepTests()
    {
        _transport.Setup(t => t.HealthChecker).Returns(_checker.Object);
        _transport.Setup(t => t.CommunicationStyle).Returns(CommunicationStyle.KubernetesApi);
        _registry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(_transport.Object);
    }

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private HealthCheckTestHost CreateHost(Mock<IRedisSafeRunner> redis = null, int parallelism = 4, TimeSpan? probeTimeout = null, HealthCheckInProcessLocks locks = null)
    {
        var host = new HealthCheckTestHost(_machines.Object, _policies.Object, _registry.Object, (redis ?? HealthCheckTestHost.PassthroughRedis()).Object, new MachineHealthCheckSetting { ProbeTimeoutSeconds = (probeTimeout ?? Fast).TotalSeconds, MaxParallelism = parallelism, SweepDeadlineSeconds = 30 }, locks);
        _hosts.Add(host);

        return host;
    }

    private void Seed(params Machine[] machines)
    {
        _machines.Setup(p => p.GetMachinesAllSpacesPagingAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((machines.Length, machines.ToList()));

        foreach (var machine in machines)
            _machines.Setup(p => p.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>())).ReturnsAsync(machine);
    }

    private static Machine DueMachine(int id) => new() { Id = id, Name = $"m{id}", IsDisabled = false, HealthLastChecked = null, Endpoint = """{"communicationStyle":"KubernetesApi"}""" };

    private void ProbeReturns(Func<Machine, CancellationToken, Task<HealthCheckResult>> probe)
        => _checker.Setup(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>())).Returns<Machine, MachineConnectivityPolicyDto, CancellationToken, MachineHealthCheckPolicyDto>((m, _, ct, _) => probe(m, ct));

    // ── lock usage ──

    [Fact]
    public async Task Sweep_TakesGlobalSweepLockAndPerMachineLocks()
    {
        var keys = new List<string>();
        Seed(DueMachine(1), DueMachine(2));
        ProbeReturns((_, _) => Task.FromResult(new HealthCheckResult(true, "ok")));

        await CreateHost(HealthCheckTestHost.PassthroughRedis(keys)).Service.AutoHealthCheckForAllAsync();

        keys.ShouldContain(MachineHealthCheckCoordinator.SweepLockKey);
        keys.ShouldContain(MachineHealthCheckCoordinator.MachineLockKey(1));
        keys.ShouldContain(MachineHealthCheckCoordinator.MachineLockKey(2));
    }

    [Fact]
    public async Task Sweep_SecondConcurrentSweep_DoesNotProbe()
    {
        var locks = new HealthCheckInProcessLocks();
        var gate = new TaskCompletionSource();
        var probes = 0;
        Seed(DueMachine(1));
        ProbeReturns(async (_, _) => { Interlocked.Increment(ref probes); await gate.Task; return new HealthCheckResult(true, "ok"); });
        var replicaA = CreateHost(locks: locks, probeTimeout: TimeSpan.FromSeconds(30));
        var replicaB = CreateHost(locks: locks, probeTimeout: TimeSpan.FromSeconds(30));

        var first = replicaA.Service.AutoHealthCheckForAllAsync();
        await WaitUntilAsync(() => probes == 1);

        await replicaB.Service.AutoHealthCheckForAllAsync();

        probes.ShouldBe(1, "a sweep that cannot take the lock must exit without probing");

        gate.SetResult();
        await first;
    }

    // ── per-probe timeout ──

    [Fact]
    public async Task Sweep_HangingProbe_RecordsUnavailableWithTimeoutReason_AndStrategyTokenIsCancelled()
    {
        var machine = DueMachine(1);
        CancellationToken seen = default;
        Seed(machine);
        ProbeReturns(async (_, ct) => { seen = ct; await Task.Delay(Timeout.Infinite, ct); return new HealthCheckResult(true, "never"); });

        await CreateHost().Service.AutoHealthCheckForAllAsync();

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
        machine.HealthDetail.ShouldContain("timed out after");
        machine.UnavailableSince.ShouldNotBeNull();
        seen.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Manual_HangingProbe_RecordsUnavailableWithTimeoutReason()
    {
        var machine = DueMachine(1);
        Seed(machine);
        ProbeReturns(async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(30)); return new HealthCheckResult(true, "never"); });

        var result = await CreateHost().Service.ManualHealthCheckAsync(1);

        result.Successful.ShouldBeFalse();
        result.Detail.ShouldContain("timed out after");
        result.ErrorCode.ShouldBe(ManualHealthCheckErrorCodes.AgentUnreachable);
        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
    }

    [Fact]
    public async Task Sweep_HangingMachine_DoesNotStarveOthers()
    {
        var machines = Enumerable.Range(1, 6).Select(DueMachine).ToArray();
        Seed(machines);
        ProbeReturns(async (m, ct) =>
        {
            if (m.Id == 1) await Task.Delay(Timeout.Infinite, ct);

            return new HealthCheckResult(true, "ok");
        });

        await CreateHost(parallelism: 2).Service.AutoHealthCheckForAllAsync();

        machines.Skip(1).ShouldAllBe(m => m.HealthStatus == MachineHealthStatus.Healthy);
        machines[0].HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
    }

    [Fact]
    public async Task Sweep_ProbesRunWithBoundedParallelism()
    {
        var current = 0;
        var peak = 0;
        Seed(Enumerable.Range(1, 12).Select(DueMachine).ToArray());
        ProbeReturns(async (_, _) =>
        {
            var now = Interlocked.Increment(ref current);
            int snapshot;
            do { snapshot = peak; } while (now > snapshot && Interlocked.CompareExchange(ref peak, now, snapshot) != snapshot);
            await Task.Delay(40);
            Interlocked.Decrement(ref current);

            return new HealthCheckResult(true, "ok");
        });

        await CreateHost(parallelism: 3, probeTimeout: TimeSpan.FromSeconds(30)).Service.AutoHealthCheckForAllAsync();

        peak.ShouldBe(3);
    }

    // ── per-machine exclusivity vs manual ──

    [Fact]
    public async Task Manual_NotBlockedByRunningSweep_OnAnotherMachine()
    {
        var gate = new TaskCompletionSource();
        var sweepProbing = new TaskCompletionSource();
        var sweepMachine = DueMachine(1);
        var manualMachine = DueMachine(2);
        _machines.Setup(p => p.GetMachinesAllSpacesPagingAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((1, new List<Machine> { sweepMachine }));
        _machines.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(sweepMachine);
        _machines.Setup(p => p.GetMachinesByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(manualMachine);
        ProbeReturns(async (m, _) =>
        {
            if (m.Id == 1) { sweepProbing.TrySetResult(); await gate.Task; }

            return new HealthCheckResult(true, "ok");
        });
        var host = CreateHost(probeTimeout: TimeSpan.FromSeconds(30));

        var sweep = host.Service.AutoHealthCheckForAllAsync();
        await sweepProbing.Task;

        var result = await host.Service.ManualHealthCheckAsync(2);

        result.Successful.ShouldBeTrue("a manual check must not wait for the global sweep");

        gate.SetResult();
        await sweep;
    }

    [Fact]
    public async Task Manual_MachineLockedByAnotherReplica_ReturnsProbeInProgress_WithoutProbing()
    {
        Seed(DueMachine(1));
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(MachineHealthCheckCoordinator.MachineLockKey(1), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ReturnsAsync((object)null);

        var result = await CreateHost(redis).Service.ManualHealthCheckAsync(1);

        result.Successful.ShouldBeFalse();
        result.ErrorCode.ShouldBe(ManualHealthCheckErrorCodes.ProbeInProgress);
        _checker.Verify(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_MachineAlreadyBeingProbed_IsSkippedNotRecorded()
    {
        var machine = DueMachine(1);
        Seed(machine);
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(MachineHealthCheckCoordinator.SweepLockKey, It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).Returns<string, Func<Task<object>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>((_, logic, _, _, _, _) => logic());
        redis.Setup(r => r.ExecuteWithLockAsync<object>(MachineHealthCheckCoordinator.MachineLockKey(1), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ReturnsAsync((object)null);

        await CreateHost(redis).Service.AutoHealthCheckForAllAsync();

        machine.HealthLastChecked.ShouldBeNull();
        _checker.Verify(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_MachineRefreshedSinceListing_IsNotProbedAgain()
    {
        var listed = DueMachine(1);
        var refreshed = DueMachine(1);
        refreshed.HealthLastChecked = DateTimeOffset.UtcNow;
        _machines.Setup(p => p.GetMachinesAllSpacesPagingAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((1, new List<Machine> { listed }));
        _machines.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(refreshed);

        await CreateHost().Service.AutoHealthCheckForAllAsync();

        _checker.Verify(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_ProbeThrows_RecordsUnavailableAndOthersStillRun()
    {
        var bad = DueMachine(1);
        var good = DueMachine(2);
        Seed(bad, good);
        ProbeReturns((m, _) => m.Id == 1 ? throw new InvalidOperationException("kaput") : Task.FromResult(new HealthCheckResult(true, "ok")));

        await CreateHost().Service.AutoHealthCheckForAllAsync();

        bad.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
        bad.HealthDetail.ShouldContain("kaput");
        good.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
    }

    [Fact]
    public async Task Sweep_CallerCancelled_RecordsNothing()
    {
        var machine = DueMachine(1);
        using var cts = new CancellationTokenSource();
        Seed(machine);
        ProbeReturns(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new HealthCheckResult(true, "never"); });

        await Should.ThrowAsync<OperationCanceledException>(() => CreateHost(probeTimeout: TimeSpan.FromSeconds(30)).Service.AutoHealthCheckForAllAsync(cts.Token));

        machine.HealthLastChecked.ShouldBeNull("an aborted sweep must not write a spurious Unavailable status");
    }

    // ── wiring: the production entry points go through the guarded sweep ──

    [Fact]
    public async Task Handler_AutoCommand_RunsTheGuardedSweep()
    {
        var keys = new List<string>();
        Seed(DueMachine(1));
        ProbeReturns((_, _) => Task.FromResult(new HealthCheckResult(true, "ok")));
        var handler = new AutoMachineHealthCheckCommandHandler(CreateHost(HealthCheckTestHost.PassthroughRedis(keys)).Service);

        await handler.Handle(null, CancellationToken.None);

        keys.ShouldContain(MachineHealthCheckCoordinator.SweepLockKey, "the recurring path must reach TryRunSweepAsync; removing the guard call site must fail this test");
        keys.ShouldContain(MachineHealthCheckCoordinator.MachineLockKey(1));
    }

    [Fact]
    public async Task RecurringJob_DispatchesAutoCommandThroughMediator()
    {
        var mediator = new Mock<Mediator.Net.IMediator>();
        var job = new MachineHealthCheckRecurringJob(mediator.Object);

        await job.Execute();

        mediator.Verify(m => m.SendAsync<AutoMachineHealthCheckCommand, AutoMachineHealthCheckResponse>(It.IsAny<AutoMachineHealthCheckCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
