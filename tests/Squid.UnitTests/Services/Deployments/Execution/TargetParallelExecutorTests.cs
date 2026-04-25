using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class TargetParallelExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyList_ReturnsEmpty()
    {
        var results = await TargetParallelExecutor.ExecuteAsync(new List<int>(), 0, (item, ct) => Task.FromResult(item), CancellationToken.None);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SingleItem_Runs()
    {
        var items = new List<int> { 42 };

        var results = await TargetParallelExecutor.ExecuteAsync(items, 0, (item, ct) => Task.FromResult(item * 2), CancellationToken.None);

        results.ShouldBe(new List<int> { 84 });
    }

    [Fact]
    public async Task ExecuteAsync_Sequential_WhenMaxParallelism1()
    {
        var concurrency = 0;
        var maxObserved = 0;
        var items = Enumerable.Range(1, 5).ToList();

        var results = await TargetParallelExecutor.ExecuteAsync(items, 1, async (item, ct) =>
        {
            var current = Interlocked.Increment(ref concurrency);
            Interlocked.Exchange(ref maxObserved, Math.Max(maxObserved, current));
            await Task.Delay(10, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrency);
            return item;
        }, CancellationToken.None);

        maxObserved.ShouldBe(1);
        results.ShouldBe(items);
    }

    [Fact]
    public async Task ExecuteAsync_AllConcurrent_WhenUnlimited()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var items = Enumerable.Range(1, 5).ToList();

        var task = TargetParallelExecutor.ExecuteAsync(items, 0, async (item, ct) =>
        {
            if (Interlocked.Increment(ref entered) >= items.Count)
                barrier.SetResult();

            await barrier.Task.ConfigureAwait(false);
            return item;
        }, CancellationToken.None);

        var results = await task;

        entered.ShouldBe(5);
        results.ShouldBe(items);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExecuteAsync_RespectsLimit(int maxParallelism)
    {
        var concurrency = 0;
        var maxObserved = 0;
        var items = Enumerable.Range(1, 10).ToList();

        var results = await TargetParallelExecutor.ExecuteAsync(items, maxParallelism, async (item, ct) =>
        {
            var current = Interlocked.Increment(ref concurrency);
            Interlocked.Exchange(ref maxObserved, Math.Max(maxObserved, current));
            await Task.Delay(50, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrency);
            return item;
        }, CancellationToken.None);

        maxObserved.ShouldBeGreaterThan(0);
        maxObserved.ShouldBeLessThanOrEqualTo(maxParallelism);
        results.ShouldBe(items);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesResultOrder()
    {
        var items = Enumerable.Range(1, 10).ToList();
        var random = new Random(42);

        var results = await TargetParallelExecutor.ExecuteAsync(items, 0, async (item, ct) =>
        {
            await Task.Delay(random.Next(10, 50), ct).ConfigureAwait(false);
            return item * 10;
        }, CancellationToken.None);

        results.ShouldBe(items.Select(i => i * 10).ToList());
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_StopsNewItems()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;
        var items = Enumerable.Range(1, 10).ToList();

        var ex = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync(items, 1, async (item, ct) =>
            {
                Interlocked.Increment(ref started);

                if (item == 2)
                    cts.Cancel();

                ct.ThrowIfCancellationRequested();
                await Task.Delay(10, ct).ConfigureAwait(false);
                return item;
            }, cts.Token);
        });

        started.ShouldBeLessThan(items.Count);
    }

    [Fact]
    public async Task ExecuteAsync_Exception_Propagates()
    {
        var items = Enumerable.Range(1, 5).ToList();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync(items, 0, async (item, ct) =>
            {
                if (item == 3)
                    throw new InvalidOperationException("boom");

                await Task.Delay(100, ct).ConfigureAwait(false);
                return item;
            }, CancellationToken.None);
        });

        ex.Message.ShouldBe("boom");
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("5", 5)]
    [InlineData("abc", 0)]
    [InlineData("-1", 0)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    public void ParseMaxParallelism(string value, int expected)
    {
        var step = new DeploymentStepDto
        {
            Properties = value == null
                ? new List<DeploymentStepPropertyDto>()
                : new List<DeploymentStepPropertyDto>
                {
                    new() { PropertyName = SpecialVariables.Step.MaxParallelism, PropertyValue = value }
                }
        };

        var result = TargetParallelExecutor.ParseMaxParallelism(step);

        result.ShouldBe(expected);
    }

    [Fact]
    public void ParseMaxParallelism_NoProperties_ReturnsZero()
    {
        var step = new DeploymentStepDto { Properties = null };

        var result = TargetParallelExecutor.ParseMaxParallelism(step);

        result.ShouldBe(0);
    }

    // ── P0-A.2 regression guard (2026-04-24 audit) ──────────────────────────────
    //
    // Pre-fix, ExecuteAllConcurrentAsync and ExecuteThrottledAsync both awaited
    // Task.WhenAll directly. Task.WhenAll rethrows only the FIRST thrown exception
    // on a faulted task set — subsequent task failures are marked "observed" and
    // then silently discarded. In a deployment context this meant: three targets
    // in the same step could all fail for different reasons, and only one target's
    // error would surface. The operator fixed the first, re-deployed, hit the
    // second, fixed that, re-deployed, hit the third — cost and confusion.
    //
    // The fix awaits all tasks then aggregates every faulted Exception.InnerExceptions
    // into a single AggregateException with a count-qualified message. Pure
    // cancellation (no actual failures, just CT.Cancel) still propagates as a clean
    // OperationCanceledException — callers that catch OCE explicitly continue to work.

    [Fact]
    public async Task ExecuteAllConcurrent_MultipleFailures_AllCollectedInAggregate()
    {
        var items = new List<int> { 1, 2, 3 };

        var ex = await Should.ThrowAsync<AggregateException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync<int, int>(items, maxParallelism: 0, async (item, ct) =>
            {
                await Task.Yield();
                throw new InvalidOperationException($"target-{item}-boom");
            }, CancellationToken.None);
        });

        ex.InnerExceptions.Count.ShouldBe(3,
            customMessage:
                "all 3 target failures must surface together. Pre-fix Task.WhenAll rethrew only " +
                "the first one — the other two were silently observed and dropped. Operators " +
                "were driven into fix-first-error / redeploy / repeat cycles.");

        var messages = ex.InnerExceptions.Select(e => e.Message).OrderBy(m => m).ToList();
        messages.ShouldBe(new List<string> { "target-1-boom", "target-2-boom", "target-3-boom" });
    }

    [Fact]
    public async Task ExecuteThrottled_MultipleFailures_AllCollectedInAggregate()
    {
        // Throttled path has its own Task.WhenAll (items > maxParallelism > 1).
        var items = Enumerable.Range(1, 5).ToList();

        var ex = await Should.ThrowAsync<AggregateException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync<int, int>(items, maxParallelism: 2, async (item, ct) =>
            {
                await Task.Yield();

                if (item % 2 == 0)
                    throw new InvalidOperationException($"target-{item}-even-fail");

                return item;
            }, CancellationToken.None);
        });

        ex.InnerExceptions.Count.ShouldBe(2,
            customMessage: "items 2 and 4 both throw — both must surface");

        ex.InnerExceptions.Select(e => e.Message).OrderBy(m => m).ToList().ShouldBe(
            new List<string> { "target-2-even-fail", "target-4-even-fail" });
    }

    [Fact]
    public async Task ExecuteAllConcurrent_FailureCountInAggregateMessage()
    {
        // Operators reading logs should immediately see "2 of 4 tasks failed" rather
        // than having to count InnerExceptions themselves.
        var items = new List<int> { 1, 2, 3, 4 };

        var ex = await Should.ThrowAsync<AggregateException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync<int, int>(items, maxParallelism: 0, async (item, ct) =>
            {
                await Task.Yield();

                if (item <= 2)
                    throw new InvalidOperationException($"fail-{item}");

                return item;
            }, CancellationToken.None);
        });

        ex.Message.ShouldContain("2",
            customMessage: "aggregate message must name how many targets failed");
        ex.Message.ShouldContain("4",
            customMessage: "aggregate message must name the total target count for context");
    }

    [Fact]
    public async Task ExecuteAllConcurrent_CancellationRequested_PropagatesOperationCanceled()
    {
        // Pure cancellation is NOT an aggregate error — only one thing happened
        // (the operator cancelled). Callers that catch OperationCanceledException
        // explicitly for "user cancelled deploy" must not find an AggregateException
        // wrapping nothing-but-OCEs. Pre-fix and post-fix share this behaviour; the
        // regression guard is that our fix didn't accidentally broaden the wrap.
        using var cts = new CancellationTokenSource();
        var items = Enumerable.Range(1, 5).ToList();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync<int, int>(items, maxParallelism: 0, async (item, ct) =>
            {
                if (item == 3)
                    cts.Cancel();

                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                return item;
            }, cts.Token);
        });
    }

    [Fact]
    public async Task ExecuteAllConcurrent_AllSucceed_ReturnsResultsInOrder()
    {
        // Happy path regression — fix must not break ordered-return semantics.
        var items = new List<int> { 1, 2, 3, 4 };

        var results = await TargetParallelExecutor.ExecuteAsync<int, int>(items, maxParallelism: 0, async (item, ct) =>
        {
            await Task.Yield();
            return item * 10;
        }, CancellationToken.None);

        results.ShouldBe(new List<int> { 10, 20, 30, 40 });
    }

    // ── Phase-6.3: global parallelism cap ─────────────────────────────────────
    //
    // Pre-fix: per-step `SemaphoreSlim(maxParallelism)` was created fresh inside
    // ExecuteThrottledAsync. With MaxParallelism=50 per step × parallel batch
    // steps × per-target HalibutScriptObserver opening main + liveness Halibut
    // connections, a 100-target deploy spawned 200+ concurrent Halibut
    // connections + 100+ DB contexts. No back-pressure across the whole fan-out.
    //
    // Fix: a process-wide SemaphoreSlim acquired by every parallel task,
    // configured by SQUID_TARGET_PARALLELISM_GLOBAL_CAP env var:
    //   - unset / blank / "auto" → ProcessorCount × 4 (sensible default)
    //   - integer literal       → that exact cap
    //   - "disabled" / "0"      → no cap (legacy behaviour, dev/test escape)

    [Fact]
    public void GlobalParallelismCapEnvVar_ConstantNamePinned()
    {
        TargetParallelExecutor.GlobalParallelismCapEnvVar
            .ShouldBe("SQUID_TARGET_PARALLELISM_GLOBAL_CAP");
    }

    [Fact]
    public async Task ExecuteAsync_GlobalCap_RestrictsConcurrencyAcrossInvocations()
    {
        // Two simultaneous ExecuteAsync calls (simulating two parallel steps in
        // a batch) must SHARE the global cap — pre-fix each call would create
        // its own per-step semaphore and the total concurrency could exceed
        // the global cap.
        try
        {
            TargetParallelExecutor.SetGlobalCapForTesting(3);

            var concurrency = 0;
            var maxObserved = 0;
            var observeLock = new object();

            async Task<int> Worker(int item, CancellationToken ct)
            {
                lock (observeLock)
                {
                    concurrency++;
                    if (concurrency > maxObserved) maxObserved = concurrency;
                }
                await Task.Delay(80, ct);
                lock (observeLock)
                {
                    concurrency--;
                }
                return item;
            }

            var items1 = Enumerable.Range(1, 8).ToList();
            var items2 = Enumerable.Range(101, 8).ToList();

            // Two concurrent calls, each with maxParallelism=8 — without a
            // global cap, total concurrency would be up to 16. With the global
            // cap of 3, total observed concurrency must stay ≤ 3.
            var t1 = TargetParallelExecutor.ExecuteAsync(items1, 8, Worker, CancellationToken.None);
            var t2 = TargetParallelExecutor.ExecuteAsync(items2, 8, Worker, CancellationToken.None);

            await Task.WhenAll(t1, t2);

            maxObserved.ShouldBeLessThanOrEqualTo(3,
                customMessage: $"global cap=3 must restrict cross-invocation concurrency; observed {maxObserved}.");
        }
        finally
        {
            TargetParallelExecutor.ResetGlobalCapForTesting();
        }
    }

    [Fact]
    public async Task ExecuteAsync_GlobalCap_DisabledMode_NoCapApplied()
    {
        // "disabled" mode = legacy behaviour; per-step cap still applies but
        // there's no global ceiling. Useful for dev/test or for operators
        // who genuinely want the old fan-out behaviour back.
        try
        {
            TargetParallelExecutor.SetGlobalCapForTesting(null);

            var concurrency = 0;
            var maxObserved = 0;
            var observeLock = new object();

            var items = Enumerable.Range(1, 12).ToList();

            await TargetParallelExecutor.ExecuteAsync<int, int>(items, maxParallelism: 0, async (item, ct) =>
            {
                lock (observeLock)
                {
                    concurrency++;
                    if (concurrency > maxObserved) maxObserved = concurrency;
                }
                await Task.Delay(20, ct);
                lock (observeLock) { concurrency--; }
                return item;
            }, CancellationToken.None);

            maxObserved.ShouldBeGreaterThan(3,
                customMessage: "with global cap disabled, per-step fan-out should NOT be globally clamped.");
        }
        finally
        {
            TargetParallelExecutor.ResetGlobalCapForTesting();
        }
    }

    [Fact]
    public async Task ExecuteAsync_GlobalCap_StillReleasesOnException()
    {
        // Resource-leak guard: if a worker throws, the global slot must still
        // be released so subsequent invocations don't deadlock.
        try
        {
            TargetParallelExecutor.SetGlobalCapForTesting(2);

            // 3 throwing tasks → AggregateException (A.2 multi-failure behaviour).
            // Either type is fine for this test; the point is that the call
            // PROPAGATES the failure, then the global slot must be free.
            await Should.ThrowAsync<Exception>(async () =>
            {
                await TargetParallelExecutor.ExecuteAsync<int, int>(
                    new List<int> { 1, 2, 3 }, maxParallelism: 0,
                    (_, _) => throw new InvalidOperationException("boom"),
                    CancellationToken.None);
            });

            // After the failed call, the cap must NOT be permanently exhausted.
            // Run a second call that needs the full cap; it must complete promptly.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var results = await TargetParallelExecutor.ExecuteAsync<int, int>(
                new List<int> { 10, 20 }, maxParallelism: 0,
                (i, ct) => Task.FromResult(i),
                cts.Token);

            results.Count.ShouldBe(2);
        }
        finally
        {
            TargetParallelExecutor.ResetGlobalCapForTesting();
        }
    }

    [Theory]
    [InlineData(null, true)]            // unset → auto default → cap exists
    [InlineData("", true)]              // blank → auto default
    [InlineData("auto", true)]          // explicit auto
    [InlineData("8", true)]             // explicit int
    [InlineData("disabled", false)]     // explicit disable
    [InlineData("0", false)]            // 0 = disable
    [InlineData("garbage", true)]       // unrecognised → fall through to auto
    public void ParseGlobalCap_Matrix(string envValue, bool capExpected)
    {
        var cap = TargetParallelExecutor.ParseGlobalCap(envValue);

        if (capExpected)
            cap.ShouldNotBeNull(customMessage: $"env value '{envValue}' should produce a cap.");
        else
            cap.ShouldBeNull(customMessage: $"env value '{envValue}' should produce NO cap (disabled mode).");
    }

    // ── Phase-7 audit follow-up: floor on the auto-derived cap ────────────────
    //
    // Pre-fix Phase-6.3 used `ProcessorCount × 4` as the auto default. On a
    // 1-vCPU pod that's 4 — so a 50-target health check serialises into 12+
    // fan-out generations × per-target Halibut polling time → minutes of
    // throttle for what should be parallel. Floor of 32 keeps small
    // containers responsive while big servers (8+ vCPU) still scale up via
    // the multiplier.
    //
    // Explicit operator-supplied integers are NOT floored — an operator
    // who deliberately sets cap=2 should get exactly 2.

    [Fact]
    public void ParseGlobalCap_AutoOnSmallContainer_AppliesFloor()
    {
        // We can't actually change ProcessorCount mid-test, but the parser
        // applies the floor unconditionally to the auto-derived value. On
        // any host with ProcessorCount < 8, the floor (32) governs.
        // On hosts with ProcessorCount >= 8, ProcessorCount × 4 >= 32 so
        // the multiplier governs. Either way, the result must be ≥ 32.
        var cap = TargetParallelExecutor.ParseGlobalCap(null);

        cap.ShouldNotBeNull();
        cap.Value.ShouldBeGreaterThanOrEqualTo(32,
            customMessage:
                "auto-derived cap floor must be ≥ 32 — small-container (1-2 vCPU) deploys " +
                "shouldn't see catastrophic throughput drop. Operators who genuinely want a " +
                "tighter cap can set SQUID_TARGET_PARALLELISM_GLOBAL_CAP=N explicitly.");
    }

    [Fact]
    public void ParseGlobalCap_ExplicitInteger_HonouredExactly_NotFloored()
    {
        // Explicit operator config wins — even tiny values are honoured.
        // The floor only applies to the auto path (unset / blank / "auto").
        TargetParallelExecutor.ParseGlobalCap("4").ShouldBe(4,
            customMessage: "explicit integer config must NOT be floored to 32 — operator chose 4 deliberately.");
        TargetParallelExecutor.ParseGlobalCap("1").ShouldBe(1);
        TargetParallelExecutor.ParseGlobalCap("100").ShouldBe(100);
    }
}
