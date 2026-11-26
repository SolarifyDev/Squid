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
        _machines.Setup(p => p.GetMachinesAllSpacesLeastRecentlyCheckedFirstAsync(It.IsAny<CancellationToken>())).ReturnsAsync(machines.ToList());

        foreach (var machine in machines)
            _machines.Setup(p => p.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>())).ReturnsAsync(machine);
    }

    private static Machine DueMachine(int id) => new() { Id = id, Name = $"m{id}", IsDisabled = false, HealthLastChecked = null, Endpoint = """{"communicationStyle":"KubernetesApi"}""" };

    private void ProbeReturns(Func<Machine, CancellationToken, Task<HealthCheckResult>> probe)
        => _checker.Setup(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>())).Returns<Machine, MachineConnectivityPolicyDto, CancellationToken, MachineHealthCheckPolicyDto>((m, _, ct, _) => probe(m, ct));

    // ── a probe never outlives the sweep that runs it ──

    private const int HeadId = 100;

    // A machine that answers at once, listed first, so with MaxParallelism 1 the machine under test is NOT the head of the sweep order:
    // it is started only through the rule the test names, never through the always-start-the-head rule.
    private static Machine FastHead() => new() { Id = HeadId, Name = "head", IsDisabled = false, HealthLastChecked = null, Endpoint = """{"communicationStyle":"KubernetesApi"}""" };

    [Theory]
    [InlineData(CommunicationStyle.TentaclePolling)]
    [InlineData(CommunicationStyle.KubernetesApi)]
    public async Task Sweep_HungProbeUnderDefaultTimeout_IsRecordedUnavailable_BeforeTheSweepDeadline(CommunicationStyle style)
    {
        var clock = new SweepClock();
        var machine = new Machine { Id = 1, Name = "hung", IsDisabled = false, HealthLastChecked = null, Endpoint = "{\"communicationStyle\":\"" + style + "\"}" };
        var started = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recordedAt = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        Seed(FastHead(), machine);
        _transport.Setup(t => t.CommunicationStyle).Returns(style);
        _machines.Setup(p => p.UpdateMachineAsync(It.Is<Machine>(m => m.Id == 1), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback(() => recordedAt.TrySetResult(clock.GetUtcNow())).Returns(Task.CompletedTask);
        ProbeReturns(async (m, ct) =>
        {
            if (m.Id == HeadId) return new HealthCheckResult(true, "ok");

            started.TrySetResult(clock.GetUtcNow());
            await Task.Delay(Timeout.Infinite, ct);
            return new HealthCheckResult(true, "unreachable");
        });
        var host = CreateClockedHost(clock);
        var timeout = host.Coordinator.ProbeTimeoutFor(style, null);

        var sweep = host.Service.AutoHealthCheckForAllAsync();
        await Task.WhenAny(sweep, started.Task).WaitAsync(Guard);

        started.Task.IsCompleted.ShouldBeTrue("a non-head probe whose full timeout plus grace fits in the sweep must start through the fit rule");
        await clock.WaitForTimersAsync(3);
        clock.Advance(timeout);
        await sweep.WaitAsync(Guard);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
        machine.HealthDetail.ShouldContain("timed out");
        (await recordedAt.Task.WaitAsync(Guard) - started.Task.Result).ShouldBe(timeout, "recorded after its FULL default timeout");
        recordedAt.Task.Result.ShouldBeLessThan(DateTimeOffset.UnixEpoch + SweepLength, "recorded before the sweep deadline");
    }

    // ── a machine's recorded health is only ever what it did within its full probe timeout ──

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepLength = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan KubernetesApiTimeout = TimeSpan.FromSeconds(75);

    private static Machine PreviouslyHealthyDueMachine(int id) => new() { Id = id, Name = $"m{id}", IsDisabled = false, HealthStatus = MachineHealthStatus.Healthy, HealthLastChecked = DateTimeOffset.UtcNow.AddHours(-2), HealthDetail = "ok earlier", Endpoint = """{"communicationStyle":"KubernetesApi"}""" };

    private HealthCheckTestHost CreateClockedHost(SweepClock clock, MachineHealthCheckSetting setting = null)
    {
        var host = new HealthCheckTestHost(_machines.Object, _policies.Object, _registry.Object, HealthCheckTestHost.PassthroughRedis().Object, setting ?? new MachineHealthCheckSetting { SweepDeadlineSeconds = SweepLength.TotalSeconds, MaxParallelism = 1 }, time: clock);
        _hosts.Add(host);

        return host;
    }

    // The sweep reloads the machine just before probing it: move the clock there so the probe asks to start with `left` of the sweep remaining.
    private void ProbeStartsWithSweepTimeLeft(Machine machine, SweepClock clock, TimeSpan left)
    {
        var moved = 0;

        _machines.Setup(p => p.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>())).Callback(() => { if (Interlocked.Exchange(ref moved, 1) == 0) clock.Advance(SweepLength - left); }).ReturnsAsync(machine);
    }

    // A healthy machine that answers after `delay` (on the fake clock), honouring cancellation like every real strategy. The fast head answers at once.
    private TaskCompletionSource SlowHealthyProbe(SweepClock clock, TimeSpan delay)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ProbeReturns(async (m, ct) =>
        {
            if (m.Id == HeadId) return new HealthCheckResult(true, "ok");

            started.TrySetResult();
            await Task.Delay(delay, clock, ct);
            return new HealthCheckResult(true, "ok");
        });

        return started;
    }

    private void VerifyNeverRecorded(int machineId) => _machines.Verify(p => p.UpdateMachineAsync(It.Is<Machine>(m => m.Id == machineId), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

    [Theory]
    [InlineData(6)]        // #627 probed it with a 1s timeout and recorded it Unavailable for a whole interval
    [InlineData(79.999)]   // one millisecond short of 75s + 5s grace
    public async Task Sweep_HealthyButSlowMachineNearTheEndOfTheSweep_IsDeferred_NeverRecordedUnavailable(double leftSeconds)
    {
        var clock = new SweepClock();
        var machine = PreviouslyHealthyDueMachine(1);
        var lastChecked = machine.HealthLastChecked;
        Seed(FastHead(), machine);
        ProbeStartsWithSweepTimeLeft(machine, clock, TimeSpan.FromSeconds(leftSeconds));
        var started = SlowHealthyProbe(clock, TimeSpan.FromSeconds(3));

        var sweep = CreateClockedHost(clock).Service.AutoHealthCheckForAllAsync();

        await Task.WhenAny(sweep, started.Task).WaitAsync(Guard);

        if (started.Task.IsCompleted)
        {
            await clock.WaitForTimersAsync(4);
            clock.Advance(TimeSpan.FromSeconds(3));
        }

        await sweep.WaitAsync(Guard);

        started.Task.IsCompleted.ShouldBeFalse("a probe without its full timeout left in the sweep must not start");
        VerifyNeverRecorded(machine.Id);
        machine.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
        machine.HealthLastChecked.ShouldBe(lastChecked);
        machine.HealthDetail.ShouldBe("ok earlier");
        machine.UnavailableSince.ShouldBeNull();
        MachineHealthCheckService.ShouldRunHealthCheck(machine, null).ShouldBeTrue("a deferred machine stays due for the next sweep");
    }

    [Fact]
    public async Task Sweep_FullTimeoutPlusGraceLeft_SlowHealthyMachine_RunsWithItsFullTimeout_AndIsRecordedHealthy()
    {
        var clock = new SweepClock();
        var machine = PreviouslyHealthyDueMachine(1);
        Seed(FastHead(), machine);
        ProbeStartsWithSweepTimeLeft(machine, clock, KubernetesApiTimeout + TimeSpan.FromSeconds(5));
        var started = SlowHealthyProbe(clock, TimeSpan.FromSeconds(70));

        var sweep = CreateClockedHost(clock).Service.AutoHealthCheckForAllAsync();

        await started.Task.WaitAsync(Guard);
        await clock.WaitForTimersAsync(4);
        clock.Advance(TimeSpan.FromSeconds(70));
        await sweep.WaitAsync(Guard);

        clock.Armed[2].ShouldBe(KubernetesApiTimeout, "the probe runs with the transport's full timeout (timers: deadline, head probe, this probe)");
        machine.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
        machine.HealthDetail.ShouldBe("ok");
    }

    // ── progress: every due machine is started within a bounded number of sweeps ──

    [Fact]
    public async Task Sweep_TimeoutThatOnlyFitsAFreshSweep_IsStillStarted_WhenListingUsedTheDifference()
    {
        // 295s + 5s grace fits a fresh 300s sweep exactly, so it is not "longer than the sweep"; but listing took 10s, so it can never fit THIS
        // sweep. Deferring it would defer it every sweep, forever. It must start (with its full timeout) instead.
        var clock = new SweepClock();
        var probed = new List<int>();
        var listed = new List<Machine> { FastHead(), DueMachine(1) };
        Seed(listed.ToArray());
        _machines.Setup(p => p.GetMachinesAllSpacesLeastRecentlyCheckedFirstAsync(It.IsAny<CancellationToken>())).Callback(() => clock.Advance(TimeSpan.FromSeconds(10))).ReturnsAsync(listed);
        ProbeReturns((m, _) => { lock (probed) probed.Add(m.Id); return Task.FromResult(new HealthCheckResult(true, "ok")); });

        await CreateClockedHost(clock, new MachineHealthCheckSetting { SweepDeadlineSeconds = 300, ProbeTimeoutSeconds = 295, MaxParallelism = 1 }).Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

        probed.ShouldBe(new List<int> { HeadId, 1 }, "the machine behind the head cannot fit this sweep at all, so it is started rather than deferred forever");
        clock.Armed.Skip(1).ShouldAllBe(t => t == TimeSpan.FromSeconds(295), "never shortened");
    }

    [Fact]
    public async Task Sweep_HeadOfTheOrder_IsStarted_EvenWhenItsOwnSetupLeavesItJustShortOfItsFullTimeout()
    {
        // 290s + 5s grace fits the probing window (300s), but reloading the machine took 6s, leaving 294s: a deferral rule alone would defer it in
        // every sweep, because every sweep spends the same setup time. The head of the least-recently-attempted order is always started.
        var clock = new SweepClock();
        var machine = DueMachine(1);
        Seed(machine);
        _machines.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).Callback(() => clock.Advance(TimeSpan.FromSeconds(6))).ReturnsAsync(machine);
        ProbeReturns((_, _) => Task.FromResult(new HealthCheckResult(true, "ok")));

        await CreateClockedHost(clock, new MachineHealthCheckSetting { SweepDeadlineSeconds = 300, ProbeTimeoutSeconds = 290, MaxParallelism = 1 }).Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
        clock.Armed.ShouldBe(new[] { SweepLength, TimeSpan.FromSeconds(290) }, "started with its full timeout");
    }

    [Fact]
    public async Task Sweep_MachinesTheDeadlineCut_GoBehindMachinesNotYetStarted_SoTheRestOfTheFleetIsChecked()
    {
        // Two machines that never answer, a timeout longer than the sweep, MaxParallelism 2: they take both slots and the deadline cuts them, so
        // nothing is recorded and their check time stays the oldest. If they went first again, the three healthy machines would never be checked.
        var clock = new SweepClock();
        var fleet = new[] { DueMachine(1), DueMachine(2), DueMachine(3), DueMachine(4), DueMachine(5) };
        var probes = new List<int>();
        var hung = 0;
        SeedOrderedLikeTheDatabase(fleet);
        ProbeReturns(async (m, ct) =>
        {
            lock (probes) probes.Add(m.Id);

            if (m.Id > 2) return new HealthCheckResult(true, "ok");

            Interlocked.Increment(ref hung);
            await Task.Delay(Timeout.Infinite, ct);
            return new HealthCheckResult(true, "never");
        });
        var host = CreateClockedHost(clock, new MachineHealthCheckSetting { SweepDeadlineSeconds = 300, ProbeTimeoutSeconds = 600, MaxParallelism = 2 });

        await RunSweepUntilHungThenPastTheDeadlineAsync(host, clock, () => hung == 2);
        probes.ShouldBe(new List<int> { 1, 2 }, ignoreOrder: true);

        await RunSweepUntilHungThenPastTheDeadlineAsync(host, clock, () => hung == 4);

        // With two slots the order probes REACH the strategy interleaves, so assert the outcome: every machine never started got its turn and a verdict.
        probes.Skip(2).ShouldBe(new List<int> { 3, 4, 5, 1, 2 }, ignoreOrder: true, "the machines the deadline cut last sweep go behind the ones never started");
        fleet.Skip(2).ShouldAllBe(m => m.HealthStatus == MachineHealthStatus.Healthy);
        VerifyNeverRecorded(1);
        VerifyNeverRecorded(2);
    }

    private async Task RunSweepUntilHungThenPastTheDeadlineAsync(HealthCheckTestHost host, SweepClock clock, Func<bool> hungProbesWaiting)
    {
        var sweep = host.Service.AutoHealthCheckForAllAsync();

        await SweepClock.WaitUntilAsync(hungProbesWaiting, "the probes that never answer to hold every slot");
        clock.Advance(SweepLength);
        await sweep.WaitAsync(Guard);
    }

    // The due list is re-read every sweep in the database's order: never checked first, then oldest check, then id.
    private void SeedOrderedLikeTheDatabase(params Machine[] machines)
    {
        _machines.Setup(p => p.GetMachinesAllSpacesLeastRecentlyCheckedFirstAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => machines.OrderBy(m => m.HealthLastChecked != null).ThenBy(m => m.HealthLastChecked).ThenBy(m => m.Id).ToList());

        foreach (var machine in machines)
            _machines.Setup(p => p.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>())).ReturnsAsync(machine);
    }

    [Fact]
    public async Task Sweep_ProbeCutByTheSweepDeadline_RecordsNothing_UnavailableSinceUntouched()
    {
        // Only reachable when the probe's timeout cannot fit in any sweep (operator-configured 600s > 300s sweep): the deadline cancels the
        // probe mid-flight. Nothing is recorded, so a previously Unavailable machine keeps its UnavailableSince (cleanup grace) exactly.
        var clock = new SweepClock();
        var unavailableSince = DateTimeOffset.UtcNow.AddDays(-1);
        var machine = PreviouslyHealthyDueMachine(1);
        machine.HealthStatus = MachineHealthStatus.Unavailable;
        machine.UnavailableSince = unavailableSince;
        Seed(machine);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeReturns(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new HealthCheckResult(true, "never"); });

        var sweep = CreateClockedHost(clock, new MachineHealthCheckSetting { SweepDeadlineSeconds = SweepLength.TotalSeconds, ProbeTimeoutSeconds = 600 }).Service.AutoHealthCheckForAllAsync();

        await started.Task.WaitAsync(Guard);
        await clock.WaitForTimersAsync(2);
        clock.Advance(SweepLength);
        await sweep.WaitAsync(Guard);

        VerifyNeverRecorded(machine.Id);
        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
        machine.UnavailableSince.ShouldBe(unavailableSince);
    }

    [Fact]
    public async Task Sweep_ProbesMachinesInTheOrderTheLeastRecentlyCheckedQueryReturns()
    {
        var order = new List<int>();
        Seed(DueMachine(3), DueMachine(1), DueMachine(2));
        ProbeReturns((m, _) => { lock (order) order.Add(m.Id); return Task.FromResult(new HealthCheckResult(true, "ok")); });

        await CreateHost(parallelism: 1).Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

        order.ShouldBe(new List<int> { 3, 1, 2 }, customMessage: "a deferred machine is first in the next sweep only if the sweep keeps the query's oldest-first order");
    }

    [Theory]
    [InlineData(CommunicationStyle.TentaclePolling)]
    [InlineData(CommunicationStyle.TentacleListening)]
    [InlineData(CommunicationStyle.KubernetesAgent)]
    [InlineData(CommunicationStyle.KubernetesApi)]
    [InlineData(CommunicationStyle.Ssh)]
    public async Task ManualCheck_LateAfterASweep_IsNeverDeferred_AndArmsTheSameFullTimeoutAsTheSweep(CommunicationStyle style)
    {
        var clock = new SweepClock();
        var machine = new Machine { Id = 1, Name = "m1", IsDisabled = false, HealthLastChecked = null, Endpoint = "{\"communicationStyle\":\"" + style + "\"}" };
        Seed(machine);
        _transport.Setup(t => t.CommunicationStyle).Returns(style);
        ProbeReturns((_, _) => Task.FromResult(new HealthCheckResult(true, "ok")));
        var host = CreateClockedHost(clock);

        await host.Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);
        clock.Advance(SweepLength - TimeSpan.FromSeconds(1));
        var manual = await host.Service.ManualHealthCheckAsync(1).WaitAsync(Guard);

        manual.Successful.ShouldBeTrue();
        var expected = host.Coordinator.ProbeTimeoutFor(style, null);
        clock.Armed.ShouldBe(new[] { SweepLength, expected, expected }, "sweep deadline, then the sweep probe and the manual probe, both with the full transport timeout");
    }

    // ── lock usage ──

    [Fact]
    public async Task Sweep_TakesGlobalSweepLockAndPerMachineLocks()
    {
        var keys = new List<string>();
        Seed(DueMachine(1), DueMachine(2));
        ProbeReturns((_, _) => Task.FromResult(new HealthCheckResult(true, "ok")));

        await CreateHost(HealthCheckTestHost.PassthroughRedis(keys)).Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

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

        try
        {
            await WaitUntilAsync(() => probes == 1);

            await replicaB.Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

            probes.ShouldBe(1, "a sweep that cannot take the lock must exit without probing");
        }
        finally
        {
            gate.TrySetResult();
        }

        await first.WaitAsync(Guard);
    }

    // ── per-probe timeout ──

    [Fact]
    public async Task Sweep_HangingProbe_RecordsUnavailableWithTimeoutReason_AndStrategyTokenIsCancelled()
    {
        var machine = DueMachine(1);
        CancellationToken seen = default;
        Seed(machine);
        ProbeReturns(async (_, ct) => { seen = ct; await Task.Delay(Timeout.Infinite, ct); return new HealthCheckResult(true, "never"); });

        await CreateHost().Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

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

        var result = await CreateHost().Service.ManualHealthCheckAsync(1).WaitAsync(Guard);

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

        await CreateHost(parallelism: 2).Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

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

        await CreateHost(parallelism: 3, probeTimeout: TimeSpan.FromSeconds(30)).Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

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
        _machines.Setup(p => p.GetMachinesAllSpacesLeastRecentlyCheckedFirstAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Machine> { sweepMachine });
        _machines.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(sweepMachine);
        _machines.Setup(p => p.GetMachinesByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(manualMachine);
        ProbeReturns(async (m, _) =>
        {
            if (m.Id == 1) { sweepProbing.TrySetResult(); await gate.Task; }

            return new HealthCheckResult(true, "ok");
        });
        var host = CreateHost(probeTimeout: TimeSpan.FromSeconds(30));

        var sweep = host.Service.AutoHealthCheckForAllAsync();

        try
        {
            await sweepProbing.Task.WaitAsync(Guard);

            var result = await host.Service.ManualHealthCheckAsync(2).WaitAsync(Guard);

            result.Successful.ShouldBeTrue("a manual check must not wait for the global sweep");
        }
        finally
        {
            gate.TrySetResult();
        }

        await sweep.WaitAsync(Guard);
    }

    [Fact]
    public async Task Manual_MachineLockedByAnotherReplica_ReturnsProbeInProgress_WithoutProbing()
    {
        Seed(DueMachine(1));
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(MachineHealthCheckCoordinator.MachineLockKey(1), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ReturnsAsync((object)null);

        var result = await CreateHost(redis).Service.ManualHealthCheckAsync(1).WaitAsync(Guard);

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

        await CreateHost(redis).Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

        machine.HealthLastChecked.ShouldBeNull();
        _checker.Verify(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_MachineRefreshedSinceListing_IsNotProbedAgain()
    {
        var listed = DueMachine(1);
        var refreshed = DueMachine(1);
        refreshed.HealthLastChecked = DateTimeOffset.UtcNow;
        _machines.Setup(p => p.GetMachinesAllSpacesLeastRecentlyCheckedFirstAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Machine> { listed });
        _machines.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(refreshed);

        await CreateHost().Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

        _checker.Verify(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_ProbeThrows_RecordsUnavailableAndOthersStillRun()
    {
        var bad = DueMachine(1);
        var good = DueMachine(2);
        Seed(bad, good);
        ProbeReturns((m, _) => m.Id == 1 ? throw new InvalidOperationException("kaput") : Task.FromResult(new HealthCheckResult(true, "ok")));

        await CreateHost().Service.AutoHealthCheckForAllAsync().WaitAsync(Guard);

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

        await Should.ThrowAsync<OperationCanceledException>(() => CreateHost(probeTimeout: TimeSpan.FromSeconds(30)).Service.AutoHealthCheckForAllAsync(cts.Token).WaitAsync(Guard));

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

        await handler.Handle(null, CancellationToken.None).WaitAsync(Guard);

        keys.ShouldContain(MachineHealthCheckCoordinator.SweepLockKey, "the recurring path must reach TryRunSweepAsync; removing the guard call site must fail this test");
        keys.ShouldContain(MachineHealthCheckCoordinator.MachineLockKey(1));
    }

    [Fact]
    public async Task RecurringJob_DispatchesAutoCommandThroughMediator()
    {
        var mediator = new Mock<Mediator.Net.IMediator>();
        var job = new MachineHealthCheckRecurringJob(mediator.Object);

        await job.Execute().WaitAsync(Guard);

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
