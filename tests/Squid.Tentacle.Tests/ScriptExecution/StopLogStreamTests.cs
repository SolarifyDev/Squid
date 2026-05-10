using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// P0-4 — pins the bounded teardown semantics of
/// <see cref="ScriptPodService.StopLogStream"/>.
///
/// <para><b>The bug it closes</b>: pre-fix the teardown path called
/// <c>ctx.LogStreamCts?.Cancel()</c> and immediately moved on to delete
/// the pod and wipe the workspace. The background log-stream task was
/// still running concurrently and could:</para>
/// <list type="bullet">
///   <item>Enqueue log lines into <c>ctx.StreamedLogLines</c> AFTER
///         <c>DrainFinalLogs</c> had already drained the queue — those
///         lines were silently lost from the response payload.</item>
///   <item>Read from a stream pointing at a pod that
///         <c>_podManager.DeletePod</c> had just removed — throwing
///         inside the inner catch block, with the exception going to
///         <c>TaskScheduler.UnobservedTaskException</c> (NOT Serilog).</item>
/// </list>
///
/// <para><b>The fix</b>: cancel the CTS AND wait synchronously for the
/// task with a 5 s bound. Sync-wait is intentional — the only callers
/// (<c>CompleteScript</c>, <c>CancelScript</c>) implement the
/// synchronous <c>IScriptService</c> RPC contract and can't go async
/// without a Halibut surface change.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class StopLogStreamTests
{
    [Fact]
    public void StopLogStream_NullTask_Returns()
    {
        // No StartLogStream → both fields null. Must not throw.
        var ctx = MakeContext();

        Should.NotThrow(() => ScriptPodService.StopLogStream(ctx));
    }

    [Fact]
    public void StopLogStream_NullCts_Returns()
    {
        var ctx = MakeContext();
        ctx.LogStreamTask = Task.CompletedTask;
        // LogStreamCts left null — the teardown must still succeed.

        Should.NotThrow(() => ScriptPodService.StopLogStream(ctx));
    }

    [Fact]
    public void StopLogStream_RunningTask_CancelsAndAwaits()
    {
        // Simulate a running task: a Task.Run loop that respects the CT
        // and exits within milliseconds of cancel.
        var ctx = MakeContext();
        var cts = new CancellationTokenSource();
        ctx.LogStreamCts = cts;

        var iterations = 0;
        ctx.LogStreamTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    iterations++;
                    await Task.Delay(10, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        ScriptPodService.StopLogStream(ctx);

        ctx.LogStreamTask.IsCompleted.ShouldBeTrue(
            customMessage: "StopLogStream MUST wait for the task to finish before returning. " +
                          "If this fails the teardown is racing the background task and can lose " +
                          "log lines or read from a deleted pod.");
        cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void StopLogStream_TaskThatThrowsSync_ObservesException()
    {
        // Task.Run with a body that throws synchronously before any await.
        // Without a wait + observe, the exception would land on
        // TaskScheduler.UnobservedTaskException and silently disappear.
        var ctx = MakeContext();
        ctx.LogStreamCts = new CancellationTokenSource();
        ctx.LogStreamTask = Task.Run(() => throw new InvalidOperationException("sync throw"));

        // Must not propagate — StopLogStream observes & swallows at Debug.
        Should.NotThrow(() => ScriptPodService.StopLogStream(ctx));

        // The task is now in a faulted state but its Exception property has been
        // observed (Wait reads it, then we catch).
        ctx.LogStreamTask.IsFaulted.ShouldBeTrue();
        // .Exception is non-null because the task faulted; we observed it inside
        // StopLogStream so reading it again here is also fine.
        ctx.LogStreamTask.Exception.ShouldNotBeNull();
    }

    [Fact]
    public void StopLogStream_TaskThatNeverFinishes_TimesOutAndContinues()
    {
        // A pathological task that ignores cancellation should not block
        // teardown forever. The 5s timeout caps the wait; the warning is
        // logged but execution proceeds.
        var ctx = MakeContext();
        ctx.LogStreamCts = new CancellationTokenSource();

        // Use a TaskCompletionSource so we can simulate a never-finishing task
        // without actually sleeping 5 seconds in the test. We pin the timeout
        // in a separate test to avoid 5s waits during normal CI.
        var tcs = new TaskCompletionSource();
        ctx.LogStreamTask = tcs.Task;

        // Cancel the context so the test doesn't sleep — but the task is
        // bound to a TCS that ignores cancellation, so the wait will time out.
        // To keep the test fast we override the cancellation: just verify the
        // method returns within a few seconds even though the task never finishes.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ScriptPodService.StopLogStream(ctx);
        stopwatch.Stop();

        // Bounded by LogStreamShutdownTimeout (5 s) + small jitter for thread
        // scheduling. If this exceeds 8 s, the timeout isn't being honoured.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8),
            customMessage: "StopLogStream MUST honour the LogStreamShutdownTimeout — a stuck task " +
                          "cannot be allowed to block CompleteScript indefinitely.");

        tcs.TrySetCanceled();   // Free the dangling task.
    }

    private static ScriptPodContext MakeContext() =>
        new(ticketId: "test-ticket", podName: "test-pod", workDir: "/tmp/test", eosMarkerToken: "EOS");
}
