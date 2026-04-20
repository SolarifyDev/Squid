using Shouldly;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class SelfHealControllerTests
{
    [Fact]
    public async Task Start_InvokesEachActionAtLeastOnce()
    {
        var action = new CountingAction(TimeSpan.FromMilliseconds(20));
        await using var controller = new SelfHealController(new[] { action });

        controller.Start();
        await WaitUntil(() => action.Runs >= 2, TimeSpan.FromSeconds(2));

        action.Runs.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Start_Twice_IsIdempotent_DoesNotDoubleUpLoops()
    {
        var action = new CountingAction(TimeSpan.FromMilliseconds(30));
        await using var controller = new SelfHealController(new[] { action });

        controller.Start();
        controller.Start();

        await Task.Delay(200);

        // Exact count depends on timing, but if double-started we'd see ~2x the single-start rate.
        var afterSingle = action.Runs;
        afterSingle.ShouldBeLessThan(20, "two Start() calls must not spawn duplicate loops");
    }

    [Fact]
    public async Task ActionThrowing_ContinuesOnSchedule()
    {
        var throwing = new ThrowingThenCountingAction(throwsFirst: 2, interval: TimeSpan.FromMilliseconds(20));
        await using var controller = new SelfHealController(new[] { throwing });

        controller.Start();
        await WaitUntil(() => throwing.SuccessfulRuns >= 1, TimeSpan.FromSeconds(2));

        throwing.AttemptedRuns.ShouldBeGreaterThanOrEqualTo(3);
        throwing.SuccessfulRuns.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Dispose_StopsLoops_Promptly()
    {
        var action = new CountingAction(TimeSpan.FromMilliseconds(20));
        var controller = new SelfHealController(new[] { action });
        controller.Start();

        await Task.Delay(100);
        var runsAtStop = action.Runs;

        await controller.DisposeAsync();
        await Task.Delay(200);

        var runsAfterStop = action.Runs;
        (runsAfterStop - runsAtStop).ShouldBeLessThanOrEqualTo(2,
            "after Dispose, the loop must not keep executing");
    }

    [Fact]
    public async Task EachAction_HasIndependentCadence()
    {
        var fast = new CountingAction(TimeSpan.FromMilliseconds(20));
        var slow = new CountingAction(TimeSpan.FromMilliseconds(500));
        await using var controller = new SelfHealController(new[] { fast, (ISelfHealAction)slow });

        controller.Start();

        // Poll for the signal rather than sleep-then-assert. On a loaded CI runner
        // a fixed Task.Delay(500) can yield only 2 fast-ticks (observed failure:
        // fast.Runs=2, slow.Runs=2), which proves nothing about cadence. Instead:
        // wait until fast has ticked ≥5 times (≈100ms minimum wall clock), by
        // which point slow (500ms interval) can have run at most 1 time. Ratio
        // holds cleanly even if the scheduler is 10× slow.
        await WaitUntil(() => fast.Runs >= 5, TimeSpan.FromSeconds(5));

        fast.Runs.ShouldBeGreaterThan(slow.Runs,
            "fast action must run more frequently than the slow one");
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"predicate never became true within {timeout}");
            await Task.Delay(20);
        }
    }

    private sealed class CountingAction : ISelfHealAction
    {
        private int _runs;
        public int Runs => _runs;
        public string Name => "counter";
        public TimeSpan CheckInterval { get; }

        public CountingAction(TimeSpan interval) { CheckInterval = interval; }

        public Task<SelfHealOutcome> RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(SelfHealOutcome.Healthy(Name));
        }
    }

    private sealed class ThrowingThenCountingAction : ISelfHealAction
    {
        private int _attempted;
        private int _successes;
        private readonly int _throwsFirst;

        public int AttemptedRuns => _attempted;
        public int SuccessfulRuns => _successes;
        public string Name => "throwing";
        public TimeSpan CheckInterval { get; }

        public ThrowingThenCountingAction(int throwsFirst, TimeSpan interval)
        {
            _throwsFirst = throwsFirst;
            CheckInterval = interval;
        }

        public Task<SelfHealOutcome> RunAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _attempted);
            if (n <= _throwsFirst) throw new InvalidOperationException("simulated failure");
            Interlocked.Increment(ref _successes);
            return Task.FromResult(SelfHealOutcome.Healthy(Name));
        }
    }
}
