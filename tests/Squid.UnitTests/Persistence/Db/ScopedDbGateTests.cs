using System.Linq;
using Shouldly;
using Squid.Core.Persistence.Db;
using Xunit;

namespace Squid.UnitTests.Persistence.Db;

public class ScopedDbGateTests
{
    [Fact]
    public async Task RunAsync_ConcurrentCallers_NeverOverlap()
    {
        var gate = new ScopedDbGate();
        var inside = 0;
        var maxInside = 0;

        async Task Work()
        {
            var now = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, now);
            await Task.Delay(5).ConfigureAwait(false);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => gate.RunAsync(Work, CancellationToken.None))).ConfigureAwait(false);

        maxInside.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_ThrowingWork_ReleasesTheGate()
    {
        var gate = new ScopedDbGate();

        await Should.ThrowAsync<InvalidOperationException>(() => gate.RunAsync<int>(() => throw new InvalidOperationException("boom"), CancellationToken.None)).ConfigureAwait(false);

        (await gate.RunAsync(() => Task.FromResult(7), CancellationToken.None).ConfigureAwait(false)).ShouldBe(7);
    }

    [Fact]
    public async Task RunAsync_CancelledWhileWaiting_ThrowsWithoutRunningWork()
    {
        var gate = new ScopedDbGate();
        var release = new TaskCompletionSource();
        var ran = false;

        var holder = gate.RunAsync(() => release.Task, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => gate.RunAsync(() => { ran = true; return Task.CompletedTask; }, cts.Token)).ConfigureAwait(false);

        ran.ShouldBeFalse();
        release.SetResult();
        await holder.ConfigureAwait(false);
    }
}
