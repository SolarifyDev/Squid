using System;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Xunit;

namespace Squid.UnitTests.Halibut.Resilience;

/// <summary>Real-time tests for the hard total-time bound (<c>boundEachAttempt</c>); a fake clock cannot race a hung call.</summary>
public sealed class RpcRetryHandlerAttemptBoundTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    // A bounded call that regresses to waiting forever must fail the test, not hang the whole run.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static RpcRetryHandler Handler() => new(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), Budget);

    private static Task<int> Hang() => new TaskCompletionSource<int>().Task;

    [Fact]
    public async Task HungAttempt_Bounded_ThrowsTransientAtBudget_InsteadOfWaiting()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Should.ThrowAsync<HalibutClientException>(() => Handler().ExecuteAsync(Hang, "CancelScript", Budget, CancellationToken.None, default, boundEachAttempt: true).WaitAsync(Guard));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        TransientFailureClassifier.IsTransient(ex).ShouldBeTrue("the runner must keep treating a lost cancel round-trip as a transient blip");
    }

    [Fact]
    public async Task HungAttempt_Unbounded_KeepsWaiting()
    {
        var call = Handler().ExecuteAsync(Hang, "GetStatus", Budget, CancellationToken.None);

        await Task.Delay(Budget * 3);

        call.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task TransientFailureThenHang_Bounded_SecondAttemptIsBoundedToo()
    {
        var budget = TimeSpan.FromMilliseconds(1500);
        var calls = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<HalibutClientException>(() => Handler().ExecuteAsync(() => ++calls == 1 ? throw new HalibutClientException("reset") : Hang(), "CancelScript", budget, CancellationToken.None, default, boundEachAttempt: true).WaitAsync(Guard));

        calls.ShouldBe(2, "the 500ms backoff fits in the budget, so a bounded second attempt runs");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task TransientFailure_BackoffWouldSpendTheRestOfTheBudget_Bounded_FailsWithoutAnUnboundedAttempt()
    {
        var calls = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Should.ThrowAsync<HalibutClientException>(() => Handler().ExecuteAsync(() => ++calls == 1 ? throw new HalibutClientException("reset") : Hang(), "CancelScript", Budget, CancellationToken.None, default, boundEachAttempt: true).WaitAsync(Guard));

        calls.ShouldBe(1, "with no budget left after the backoff, a further attempt could only run unbounded and hang on a stuck agent");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        TransientFailureClassifier.IsTransient(ex).ShouldBeTrue();
    }

    [Fact]
    public async Task AttemptThatFinishesInBudget_Bounded_ReturnsItsResult()
    {
        var result = await Handler().ExecuteAsync(async () => { await Task.Delay(20); return 7; }, "CancelScript", Budget, CancellationToken.None, default, boundEachAttempt: true);

        result.ShouldBe(7);
    }

    [Fact]
    public async Task ExhaustedBudgetBeforeFirstAttempt_Bounded_StillRunsOnceUnbounded()
    {
        var result = await Handler().ExecuteAsync(async () => { await Task.Delay(50); return 9; }, "GetStatus", TimeSpan.FromSeconds(-1), CancellationToken.None, default, boundEachAttempt: true);

        result.ShouldBe(9);
    }
}
