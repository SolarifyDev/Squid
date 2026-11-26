using System.Linq;
using Autofac;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Services.Machines.HealthCheck.Exceptions;
using Squid.Core.Settings.Machines;
using Microsoft.Extensions.Configuration;
using Squid.Message.Enums.Caching;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

public class MachineHealthCheckCoordinatorTests : IDisposable
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(200);

    private readonly IContainer _container = new ContainerBuilder().Build();

    public void Dispose() => _container.Dispose();

    private MachineHealthCheckCoordinator Create(Mock<IRedisSafeRunner> redis = null, HealthCheckInProcessLocks locks = null, int parallelism = 4, TimeSpan? probeTimeout = null, TimeSpan? sweepDeadline = null, TimeProvider time = null)
    {
        var options = new MachineHealthCheckSetting { ProbeTimeoutSeconds = (probeTimeout ?? Fast).TotalSeconds, MaxParallelism = parallelism, SweepDeadlineSeconds = (sweepDeadline ?? TimeSpan.FromSeconds(30)).TotalSeconds };

        return new MachineHealthCheckCoordinator((redis ?? HealthCheckTestHost.PassthroughRedis()).Object, _container, locks ?? new HealthCheckInProcessLocks(), options, time);
    }

    // ── appsettings binding: section/key names, defaults, fallback ──

    private static MachineHealthCheckSetting Bind(Dictionary<string, string> values)
        => new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Setting_SectionAndKeyNames_Pinned()
    {
        MachineHealthCheckSetting.SectionName.ShouldBe("MachineHealthCheck");

        var setting = Bind(new Dictionary<string, string> { ["MachineHealthCheck:ProbeTimeoutSeconds"] = "11", ["MachineHealthCheck:MaxParallelism"] = "3", ["MachineHealthCheck:SweepDeadlineSeconds"] = "22" });

        setting.ProbeTimeout.ShouldBe(TimeSpan.FromSeconds(11));
        setting.MaxParallelism.ShouldBe(3);
        setting.SweepDeadline.ShouldBe(TimeSpan.FromSeconds(22));
    }

    [Fact]
    public void Setting_Absent_UsesDefaults()
    {
        var setting = Bind(new Dictionary<string, string>());

        setting.ProbeTimeoutSeconds.ShouldBe(75);
        setting.MaxParallelism.ShouldBe(6);
        setting.SweepDeadlineSeconds.ShouldBe(300);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Setting_NonPositive_FallsBackToDefaults(string raw)
    {
        var setting = Bind(new Dictionary<string, string> { ["MachineHealthCheck:ProbeTimeoutSeconds"] = raw, ["MachineHealthCheck:MaxParallelism"] = raw, ["MachineHealthCheck:SweepDeadlineSeconds"] = raw });

        setting.ProbeTimeoutSeconds.ShouldBe(75);
        setting.MaxParallelism.ShouldBe(6);
        setting.SweepDeadlineSeconds.ShouldBe(300);
    }

    [Fact]
    public void LockKeys_Pinned()
    {
        MachineHealthCheckCoordinator.SweepLockKey.ShouldBe("squid:health:sweep");
        MachineHealthCheckCoordinator.MachineLockKey(42).ShouldBe("squid:health:machine:42");
    }

    // ── Sweep lock ──

    [Fact]
    public async Task Sweep_AcquiresSweepLockKey_AndRunsOnce()
    {
        var keys = new List<string>();
        var coordinator = Create(HealthCheckTestHost.PassthroughRedis(keys));
        var runs = 0;

        var ran = await coordinator.TryRunSweepAsync(_ => { runs++; return Task.CompletedTask; }, CancellationToken.None).WaitAsync(Guard);

        ran.ShouldBeTrue();
        runs.ShouldBe(1);
        keys.ShouldBe(new[] { MachineHealthCheckCoordinator.SweepLockKey });
    }

    [Fact]
    public async Task Sweep_ThrowingSweep_PropagatesAndReleasesLock()
    {
        var coordinator = Create();

        await Should.ThrowAsync<InvalidOperationException>(() => coordinator.TryRunSweepAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None).WaitAsync(Guard));

        var ran = await coordinator.TryRunSweepAsync(_ => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard);

        ran.ShouldBeTrue("a sweep that threw must release the in-process lock so the next sweep can run");
    }

    [Fact]
    public async Task Sweep_SecondConcurrentSweepInSameProcess_IsSkipped()
    {
        var locks = new HealthCheckInProcessLocks();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var first = Create(locks: locks);
        var second = Create(locks: locks);
        var secondRan = false;

        var firstSweep = first.TryRunSweepAsync(async _ => { started.SetResult(); await gate.Task; }, CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(Guard);

            var ran = await second.TryRunSweepAsync(_ => { secondRan = true; return Task.CompletedTask; }, CancellationToken.None).WaitAsync(Guard);

            ran.ShouldBeFalse();
            secondRan.ShouldBeFalse();
        }
        finally
        {
            gate.TrySetResult();
        }

        (await firstSweep.WaitAsync(Guard)).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_LockHeldByAnotherReplica_IsSkipped()
    {
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ReturnsAsync((object)null);
        var ran = false;

        var result = await Create(redis).TryRunSweepAsync(_ => { ran = true; return Task.CompletedTask; }, CancellationToken.None).WaitAsync(Guard);

        result.ShouldBeFalse();
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task Sweep_LockTtl_CoversDeadlinePlusMargin()
    {
        TimeSpan? expiry = null;
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>()))
            .Returns<string, Func<Task<object>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>((_, logic, e, _, _, _) => { expiry = e; return logic(); });

        await Create(redis, sweepDeadline: TimeSpan.FromSeconds(300)).TryRunSweepAsync(_ => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard);

        expiry.ShouldNotBeNull();
        expiry.Value.ShouldBeGreaterThan(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task Sweep_RedisInfrastructureFailure_FallsBackToInProcessLock()
    {
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ThrowsAsync(new LockAcquireFailedException("k", new Exception("redis down")));
        var runs = 0;

        var ran = await Create(redis).TryRunSweepAsync(_ => { runs++; return Task.CompletedTask; }, CancellationToken.None).WaitAsync(Guard);

        ran.ShouldBeTrue();
        runs.ShouldBe(1);
    }

    [Fact]
    public async Task Machine_WaitIsOneBudgetAcrossInProcessAndRedisLocks()
    {
        TimeSpan? redisWait = null;
        var locks = new HealthCheckInProcessLocks();
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>()))
            .Returns<string, Func<Task<object>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>((_, logic, _, w, _, _) => { redisWait = w; return logic(); });

        await locks.TryEnterMachineAsync(7, TimeSpan.Zero, CancellationToken.None).WaitAsync(Guard);
        var releaser = Task.Run(async () => { await Task.Delay(700); locks.ExitMachine(7); });

        var ran = await Create(redis, locks).TryRunForMachineAsync(7, TimeSpan.FromSeconds(2), () => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard);
        await releaser.WaitAsync(Guard);

        ran.ShouldBeTrue();
        redisWait.ShouldNotBeNull();
        redisWait.Value.ShouldBeLessThan(TimeSpan.FromSeconds(1.5), "the in-process wait already consumed part of the caller's 2s budget");
    }

    [Fact]
    public async Task Machine_RedisInfrastructureFailure_FallsBackToInProcessLock()
    {
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ThrowsAsync(new LockAcquireFailedException("k", new Exception("redis down")));
        var runs = 0;

        var ran = await Create(redis).TryRunForMachineAsync(7, TimeSpan.FromSeconds(1), () => { runs++; return Task.CompletedTask; }, CancellationToken.None).WaitAsync(Guard);

        ran.ShouldBeTrue();
        runs.ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_ExceedsDeadline_EndsQuietlyAndTokenWasCancelled()
    {
        var sawCancel = false;

        var ran = await Create(sweepDeadline: Fast).TryRunSweepAsync(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { sawCancel = true; throw; }
        }, CancellationToken.None).WaitAsync(Guard);

        ran.ShouldBeTrue();
        sawCancel.ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => Create().TryRunSweepAsync(token => Task.Delay(Timeout.Infinite, token), cts.Token));
    }

    // ── a sweep never shortens a probe: it either runs with its full timeout or is deferred ──

    private static readonly TimeSpan SweepDeadline = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan FullTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static Task<HealthCheckResult> HangUntilCancelled(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => new HealthCheckResult(false, "never"), TaskScheduler.Default);

    // Runs one probe inside a sweep whose clock has moved so that exactly `left` of the sweep remains when the probe asks to start,
    // then (if it started) moves time by the probe's timeout.
    private async Task<(Exception Thrown, bool Started, SweepClock Clock)> ProbeWithSweepTimeLeftAsync(TimeSpan left, TimeSpan timeout, TimeSpan? probeTimeoutSetting = null)
    {
        var clock = new SweepClock();
        var coordinator = Create(sweepDeadline: SweepDeadline, probeTimeout: probeTimeoutSetting, time: clock);
        var started = false;
        Exception thrown = null;

        await coordinator.TryRunSweepAsync(async token =>
        {
            clock.Advance(SweepDeadline - left);

            var probe = Record(() => coordinator.RunProbeAsync(ct => { started = true; return HangUntilCancelled(ct); }, token, timeout));

            if (started)
            {
                await clock.WaitForTimersAsync(2);
                clock.Advance(timeout);
            }

            thrown = await probe.WaitAsync(Guard);
        }, CancellationToken.None).WaitAsync(Guard);

        return (thrown, started, clock);
    }

    [Theory]
    [InlineData(64.999)]   // one millisecond short of the full 60s timeout + 5s grace
    [InlineData(30)]       // #627 ran it with a 25s timeout
    [InlineData(6)]        // #627 ran it with a 1s timeout and recorded a healthy machine Unavailable
    [InlineData(0)]
    public async Task Probe_LessThanItsFullTimeoutPlusGraceLeftInTheSweep_IsDeferred_NeverStarted(double leftSeconds)
    {
        var (thrown, started, clock) = await ProbeWithSweepTimeLeftAsync(TimeSpan.FromSeconds(leftSeconds), FullTimeout).WaitAsync(Guard);

        thrown.ShouldBeOfType<SweepBudgetExhaustedException>();
        started.ShouldBeFalse("a probe that cannot run its full timeout before the sweep ends must not start at all");
        clock.Armed.ShouldBe(new[] { SweepDeadline }, "only the sweep deadline is armed: no shortened probe timer");
    }

    [Theory]
    [InlineData(65)]       // exactly the full timeout + grace
    [InlineData(120)]
    [InlineData(300)]      // the very start of the sweep
    public async Task Probe_FullTimeoutPlusGraceLeftInTheSweep_RunsWithItsExactFullTimeout(double leftSeconds)
    {
        var (thrown, started, clock) = await ProbeWithSweepTimeLeftAsync(TimeSpan.FromSeconds(leftSeconds), FullTimeout).WaitAsync(Guard);

        thrown.ShouldBeNull();
        started.ShouldBeTrue();
        clock.Armed[1].ShouldBe(FullTimeout, "the probe timer must be the transport's full timeout, never what the sweep has left");
    }

    [Fact]
    public async Task Probe_WhoseTimeoutCanNeverFitInASweep_StillRuns_WithItsFullTimeout_AsBefore()
    {
        // An operator-configured timeout longer than the whole sweep can never be guaranteed to finish inside it. It runs as it always did
        // (never shortened); if the sweep ends first the probe is cut and reports nothing.
        var tooLong = TimeSpan.FromSeconds(600);

        var (thrown, started, clock) = await ProbeWithSweepTimeLeftAsync(TimeSpan.FromSeconds(100), tooLong, probeTimeoutSetting: tooLong).WaitAsync(Guard);

        started.ShouldBeTrue("deferring it would mean the sweep never checks this machine at all");
        clock.Armed[1].ShouldBe(tooLong);
        thrown.ShouldBeAssignableTo<OperationCanceledException>("the sweep deadline cut the probe, so it must report nothing rather than a result");
    }

    [Fact]
    public async Task Probe_FanOutByTheSweep_EachSeesTheSweepDeadline()
    {
        // Parallelism 2: items 1 and 2 are the head of the sweep order and do not probe; item 1 spends the sweep down to 10s. Items 3 and 4 could have
        // fitted the probing time (it began with the whole sweep left) but not what is left, and are not the head: both must be deferred.
        var clock = new SweepClock();
        var coordinator = Create(sweepDeadline: SweepDeadline, parallelism: 2, time: clock);
        var spent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thrown = new List<Exception>();

        await coordinator.TryRunSweepAsync(token => coordinator.ForEachInScopeAsync(new[] { 1, 2, 3, 4 }, async (_, item, ct) =>
        {
            if (item == 1) { clock.Advance(SweepDeadline - TimeSpan.FromSeconds(10)); spent.SetResult(); return; }

            await spent.Task.WaitAsync(Guard);

            if (item == 2) return;

            var ex = await Record(() => coordinator.RunProbeAsync(HangUntilCancelled, ct, FullTimeout));
            lock (thrown) thrown.Add(ex);
        }, token), CancellationToken.None).WaitAsync(Guard);

        thrown.Count.ShouldBe(2);
        thrown.ShouldAllBe(e => e is SweepBudgetExhaustedException, "the deadline must flow into every parallel probe the sweep starts");
    }

    [Theory]
    [InlineData(0, true)]     // the head of the sweep order is always started
    [InlineData(1, true)]     // MaxParallelism 2: the first two positions are the head
    [InlineData(2, false)]    // behind the head, a probe that could have fitted this sweep's probing time is deferred
    public async Task Probe_ShortOfItsFullTimeout_IsStartedOnlyAtTheHeadOfTheSweepOrder(int position, bool started)
    {
        var clock = new SweepClock();
        var coordinator = Create(sweepDeadline: SweepDeadline, parallelism: 2, time: clock);
        var ran = false;
        Exception thrown = null;

        await coordinator.TryRunSweepAsync(token => coordinator.ForEachInScopeAsync(new[] { 0, 1, 2 }, async (_, item, ct) =>
        {
            if (item != position) return;

            clock.Advance(SweepDeadline - TimeSpan.FromSeconds(30));
            thrown = await Record(() => coordinator.RunProbeAsync(_ => { ran = true; return Task.FromResult(new HealthCheckResult(true, "ok")); }, ct, FullTimeout));
        }, token), CancellationToken.None).WaitAsync(Guard);

        ran.ShouldBe(started);
        (thrown is SweepBudgetExhaustedException).ShouldBe(!started);
    }

    [Fact]
    public async Task Probe_ThatCannotFitTheProbingTimeLeftAfterListing_IsStarted_NotDeferredForever()
    {
        // 60s + 5s grace fits the 300s sweep, but listing used 250s: no position in this sweep could ever fit it. Deferring it would defer it every sweep.
        var clock = new SweepClock();
        var coordinator = Create(sweepDeadline: SweepDeadline, parallelism: 1, time: clock);
        var ran = new List<int>();

        await coordinator.TryRunSweepAsync(async token =>
        {
            clock.Advance(TimeSpan.FromSeconds(250));

            await coordinator.ForEachInScopeAsync(new[] { 0, 1, 2 }, async (_, item, ct) => await coordinator.RunProbeAsync(_ => { lock (ran) ran.Add(item); return Task.FromResult(new HealthCheckResult(true, "ok")); }, ct, FullTimeout), token);
        }, CancellationToken.None).WaitAsync(Guard);

        ran.ShouldBe(new[] { 0, 1, 2 });
        clock.Armed.Skip(1).ShouldAllBe(t => t == FullTimeout, "never shortened");
    }

    [Fact]
    public void SweepOrder_IsLeastRecentlyAttemptedFirst_StableAmongEquals()
    {
        var coordinator = Create();
        var now = DateTimeOffset.UtcNow;
        var neverCheckedButCut = new Squid.Core.Persistence.Entities.Deployments.Machine { Id = 1, HealthLastChecked = null };
        var neverChecked = new Squid.Core.Persistence.Entities.Deployments.Machine { Id = 2, HealthLastChecked = null };
        var checkedLongAgoButCutRecently = new Squid.Core.Persistence.Entities.Deployments.Machine { Id = 3, HealthLastChecked = now.AddHours(-5) };
        var checkedTwoHoursAgo = new Squid.Core.Persistence.Entities.Deployments.Machine { Id = 4, HealthLastChecked = now.AddHours(-2) };
        var checkedAfterItsLastStart = new Squid.Core.Persistence.Entities.Deployments.Machine { Id = 5, HealthLastChecked = now.AddHours(-3) };
        coordinator.NoteProbeStarted(1, now.AddHours(-1));
        coordinator.NoteProbeStarted(3, now.AddMinutes(-1));
        coordinator.NoteProbeStarted(5, now.AddHours(-4));

        var order = coordinator.InSweepOrder(new[] { neverCheckedButCut, neverChecked, checkedLongAgoButCutRecently, checkedAfterItsLastStart, checkedTwoHoursAgo });

        order.ShouldBe(new[] { 2, 5, 4, 1, 3 });
    }

    [Fact]
    public async Task Probe_OutsideASweep_KeepsItsFullTimeout_EvenAfterASweepRan()
    {
        var clock = new SweepClock();
        var coordinator = Create(sweepDeadline: SweepDeadline, time: clock);

        await coordinator.TryRunSweepAsync(_ => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard);

        clock.Advance(TimeSpan.FromHours(1));

        var result = await coordinator.RunProbeAsync(_ => Task.FromResult(new HealthCheckResult(true, "ok")), CancellationToken.None, FullTimeout).WaitAsync(Guard);

        result.Healthy.ShouldBeTrue("a manual check is never deferred and the sweep deadline must not leak out of the sweep");
        clock.Armed.ShouldBe(new[] { SweepDeadline, FullTimeout });
    }

    [Fact]
    public async Task Probe_CallerCancelledWhileWaitingForTheTimedOutProbesOwnError_ReportsNothing()
    {
        // The probe timed out, and while the coordinator waits its short grace for the probe's own error text the caller (the sweep deadline) is
        // cancelled. An Unavailable verdict produced after the caller gave up must not be reported, or the sweep would record it.
        var clock = new SweepClock();
        var coordinator = Create(time: clock);
        var hung = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();

        var probe = coordinator.RunProbeAsync(_ => hung.Task, caller.Token, FullTimeout);

        await clock.WaitForTimersAsync(1);
        clock.Advance(FullTimeout);
        await clock.WaitForTimersAsync(2);

        caller.Cancel();
        hung.SetResult(new HealthCheckResult(false, "connection refused"));

        await Should.ThrowAsync<OperationCanceledException>(() => probe.WaitAsync(Guard));
    }

    private static async Task<Exception> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    // ── Per-machine lock ──

    [Fact]
    public async Task Machine_SecondProbeOfSameMachine_IsSkippedWhileFirstRuns_OtherMachineIsNot()
    {
        var keys = new List<string>();
        var coordinator = Create(HealthCheckTestHost.PassthroughRedis(keys));
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var first = coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, async () => { started.SetResult(); await gate.Task; }, CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(Guard);

            (await coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard)).ShouldBeFalse("same machine must never be probed concurrently");
            (await coordinator.TryRunForMachineAsync(8, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard)).ShouldBeTrue();
        }
        finally
        {
            gate.TrySetResult();
        }

        (await first.WaitAsync(Guard)).ShouldBeTrue();
        keys.ShouldContain(MachineHealthCheckCoordinator.MachineLockKey(7));
    }

    [Fact]
    public async Task Machine_WaitingCaller_RunsAfterHolderReleases()
    {
        var coordinator = Create();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var order = new List<string>();

        var first = coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, async () => { started.SetResult(); await gate.Task; order.Add("first"); }, CancellationToken.None);
        Task<bool> waiter;

        try
        {
            await started.Task.WaitAsync(Guard);

            waiter = coordinator.TryRunForMachineAsync(7, TimeSpan.FromSeconds(10), () => { order.Add("second"); return Task.CompletedTask; }, CancellationToken.None);
            await Task.Delay(100);
        }
        finally
        {
            gate.TrySetResult();
        }

        (await waiter.WaitAsync(Guard)).ShouldBeTrue();
        await first.WaitAsync(Guard);
        order.ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public async Task Machine_ThrowingWork_ReleasesLock()
    {
        var coordinator = Create();

        await Should.ThrowAsync<InvalidOperationException>(() => coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, () => throw new InvalidOperationException("boom"), CancellationToken.None).WaitAsync(Guard));

        (await coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard)).ShouldBeTrue();
    }

    [Fact]
    public async Task Machine_LockHeldByAnotherReplica_ReturnsFalse()
    {
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ReturnsAsync((object)null);

        (await Create(redis).TryRunForMachineAsync(7, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None).WaitAsync(Guard)).ShouldBeFalse();
    }

    // ── Probe timeout + cancellation ──

    [Fact]
    public async Task Probe_StrategyHonouringToken_TimesOutWithClearReasonAndTokenIsCancelled()
    {
        CancellationToken seen = default;

        var result = await Create().RunProbeAsync(async token =>
        {
            seen = token;
            await Task.Delay(Timeout.Infinite, token);
            return new HealthCheckResult(true, "never");
        }, CancellationToken.None).WaitAsync(Guard);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("timed out after");
        seen.IsCancellationRequested.ShouldBeTrue("the strategy must receive a token that is cancelled on timeout");
    }

    [Fact]
    public async Task Probe_StrategyIgnoringToken_StillTimesOut()
    {
        var result = await Create().RunProbeAsync(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return new HealthCheckResult(true, "never");
        }, CancellationToken.None).WaitAsync(Guard);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("timed out after");
    }

    [Fact]
    public async Task Probe_StrategySwallowsCancellationAsUnhealthy_ReasonIsStillTimeout()
    {
        var result = await Create().RunProbeAsync(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException ex) { return new HealthCheckResult(false, $"connectivity failed: {ex.Message}"); }

            return new HealthCheckResult(true, "never");
        }, CancellationToken.None).WaitAsync(Guard);

        result.Detail.ShouldContain("timed out after");
    }

    [Fact]
    public async Task Probe_GenuineUnhealthyResult_IsReturnedUnchanged()
    {
        var result = await Create(probeTimeout: TimeSpan.FromSeconds(30)).RunProbeAsync(_ => Task.FromResult(new HealthCheckResult(false, "connection refused")), CancellationToken.None).WaitAsync(Guard);

        result.Detail.ShouldBe("connection refused");
    }

    [Fact]
    public async Task Probe_CallerCancellation_PropagatesInsteadOfRecordingTimeout()
    {
        using var cts = new CancellationTokenSource();
        var probeStarted = new TaskCompletionSource();

        var probe = Create(probeTimeout: TimeSpan.FromSeconds(30)).RunProbeAsync(async token =>
        {
            probeStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HealthCheckResult(true, "never");
        }, cts.Token);

        await probeStarted.Task.WaitAsync(Guard);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => probe.WaitAsync(Guard));
    }

    [Fact]
    public async Task Probe_CallerCancelledAndStrategySwallowsIt_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => Create(probeTimeout: TimeSpan.FromSeconds(30)).RunProbeAsync(_ => Task.FromResult(new HealthCheckResult(false, "A task was canceled")), cts.Token).WaitAsync(Guard));
    }

    // ── Bounded parallelism ──

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ForEach_NeverExceedsMaxParallelism_AndReachesIt(int max)
    {
        var current = 0;
        var peak = 0;

        await Create(parallelism: max).ForEachInScopeAsync(Enumerable.Range(0, 24).ToList(), async (_, _, _) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref current);
        }, CancellationToken.None).WaitAsync(Guard);

        peak.ShouldBe(max);
    }

    [Fact]
    public async Task ForEach_FailingItem_DoesNotStopOthers()
    {
        var done = new List<int>();

        await Create().ForEachInScopeAsync(Enumerable.Range(0, 10).ToList(), (_, item, _) =>
        {
            if (item == 3) throw new InvalidOperationException("boom");
            lock (done) done.Add(item);
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(Guard);

        done.Count.ShouldBe(9);
    }

    [Fact]
    public async Task ForEach_GivesEachItemItsOwnScope()
    {
        var scopes = new List<ILifetimeScope>();

        await Create().ForEachInScopeAsync(new[] { 1, 2, 3 }, (scope, _, _) => { lock (scopes) scopes.Add(scope); return Task.CompletedTask; }, CancellationToken.None).WaitAsync(Guard);

        scopes.Distinct().Count().ShouldBe(3);
        scopes.ShouldNotContain(_container);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        do { snapshot = target; } while (value > snapshot && Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }
}
