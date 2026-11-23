using System.Linq;
using Autofac;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Microsoft.Extensions.Configuration;
using Squid.Message.Enums.Caching;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

public class MachineHealthCheckCoordinatorTests : IDisposable
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(200);

    private readonly IContainer _container = new ContainerBuilder().Build();

    public void Dispose() => _container.Dispose();

    private MachineHealthCheckCoordinator Create(Mock<IRedisSafeRunner> redis = null, HealthCheckInProcessLocks locks = null, int parallelism = 4, TimeSpan? probeTimeout = null, TimeSpan? sweepDeadline = null)
    {
        var options = new MachineHealthCheckSetting { ProbeTimeoutSeconds = (probeTimeout ?? Fast).TotalSeconds, MaxParallelism = parallelism, SweepDeadlineSeconds = (sweepDeadline ?? TimeSpan.FromSeconds(30)).TotalSeconds };

        return new MachineHealthCheckCoordinator((redis ?? HealthCheckTestHost.PassthroughRedis()).Object, _container, locks ?? new HealthCheckInProcessLocks(), options);
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

        var ran = await coordinator.TryRunSweepAsync(_ => { runs++; return Task.CompletedTask; }, CancellationToken.None);

        ran.ShouldBeTrue();
        runs.ShouldBe(1);
        keys.ShouldBe(new[] { MachineHealthCheckCoordinator.SweepLockKey });
    }

    [Fact]
    public async Task Sweep_ThrowingSweep_PropagatesAndReleasesLock()
    {
        var coordinator = Create();

        await Should.ThrowAsync<InvalidOperationException>(() => coordinator.TryRunSweepAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var ran = await coordinator.TryRunSweepAsync(_ => Task.CompletedTask, CancellationToken.None);

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
        await started.Task;

        var ran = await second.TryRunSweepAsync(_ => { secondRan = true; return Task.CompletedTask; }, CancellationToken.None);

        ran.ShouldBeFalse();
        secondRan.ShouldBeFalse();

        gate.SetResult();
        (await firstSweep).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_LockHeldByAnotherReplica_IsSkipped()
    {
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ReturnsAsync((object)null);
        var ran = false;

        var result = await Create(redis).TryRunSweepAsync(_ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

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

        await Create(redis, sweepDeadline: TimeSpan.FromSeconds(300)).TryRunSweepAsync(_ => Task.CompletedTask, CancellationToken.None);

        expiry.ShouldNotBeNull();
        expiry.Value.ShouldBeGreaterThan(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task Sweep_RedisInfrastructureFailure_FallsBackToInProcessLock()
    {
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ThrowsAsync(new LockAcquireFailedException("k", new Exception("redis down")));
        var runs = 0;

        var ran = await Create(redis).TryRunSweepAsync(_ => { runs++; return Task.CompletedTask; }, CancellationToken.None);

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
        }, CancellationToken.None);

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

    // ── Per-machine lock ──

    [Fact]
    public async Task Machine_SecondProbeOfSameMachine_IsSkippedWhileFirstRuns_OtherMachineIsNot()
    {
        var keys = new List<string>();
        var coordinator = Create(HealthCheckTestHost.PassthroughRedis(keys));
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var first = coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, async () => { started.SetResult(); await gate.Task; }, CancellationToken.None);
        await started.Task;

        (await coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None)).ShouldBeFalse("same machine must never be probed concurrently");
        (await coordinator.TryRunForMachineAsync(8, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None)).ShouldBeTrue();

        gate.SetResult();
        (await first).ShouldBeTrue();
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
        await started.Task;

        var waiter = coordinator.TryRunForMachineAsync(7, TimeSpan.FromSeconds(10), () => { order.Add("second"); return Task.CompletedTask; }, CancellationToken.None);
        await Task.Delay(100);
        gate.SetResult();

        (await waiter).ShouldBeTrue();
        await first;
        order.ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public async Task Machine_ThrowingWork_ReleasesLock()
    {
        var coordinator = Create();

        await Should.ThrowAsync<InvalidOperationException>(() => coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, () => throw new InvalidOperationException("boom"), CancellationToken.None));

        (await coordinator.TryRunForMachineAsync(7, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Machine_LockHeldByAnotherReplica_ReturnsFalse()
    {
        var redis = new Mock<IRedisSafeRunner>();
        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>())).ReturnsAsync((object)null);

        (await Create(redis).TryRunForMachineAsync(7, TimeSpan.Zero, () => Task.CompletedTask, CancellationToken.None)).ShouldBeFalse();
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
        }, CancellationToken.None);

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
        }, CancellationToken.None);

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
        }, CancellationToken.None);

        result.Detail.ShouldContain("timed out after");
    }

    [Fact]
    public async Task Probe_GenuineUnhealthyResult_IsReturnedUnchanged()
    {
        var result = await Create(probeTimeout: TimeSpan.FromSeconds(30)).RunProbeAsync(_ => Task.FromResult(new HealthCheckResult(false, "connection refused")), CancellationToken.None);

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

        await probeStarted.Task;
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => probe);
    }

    [Fact]
    public async Task Probe_CallerCancelledAndStrategySwallowsIt_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => Create(probeTimeout: TimeSpan.FromSeconds(30)).RunProbeAsync(_ => Task.FromResult(new HealthCheckResult(false, "A task was canceled")), cts.Token));
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
        }, CancellationToken.None);

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
        }, CancellationToken.None);

        done.Count.ShouldBe(9);
    }

    [Fact]
    public async Task ForEach_GivesEachItemItsOwnScope()
    {
        var scopes = new List<ILifetimeScope>();

        await Create().ForEachInScopeAsync(new[] { 1, 2, 3 }, (scope, _, _) => { lock (scopes) scopes.Add(scope); return Task.CompletedTask; }, CancellationToken.None);

        scopes.Distinct().Count().ShouldBe(3);
        scopes.ShouldNotContain(_container);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        do { snapshot = target; } while (value > snapshot && Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }
}
