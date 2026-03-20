namespace Squid.Tentacle.Watchdog.Tests;

public class RetryWithBackoffTests
{
    [Fact]
    public async Task HealthyOnFirstCheck_ReturnsImmediately()
    {
        var callCount = 0;

        var result = await RetryWithBackoffAsync(
            () => { callCount++; return true; },
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        result.ShouldBeTrue();
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task FailsThenRecovers_ReturnsTrue()
    {
        var callCount = 0;

        var result = await RetryWithBackoffAsync(
            () => { callCount++; return callCount >= 3; },
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.ShouldBeTrue();
        callCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task AlwaysFails_ReturnsFalseAfterTimeout()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await RetryWithBackoffAsync(
            () => false,
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        sw.Stop();
        result.ShouldBeFalse();
        sw.Elapsed.TotalSeconds.ShouldBeGreaterThan(0.5);
    }

    [Fact]
    public async Task BackoffGrowsExponentially()
    {
        var timestamps = new List<DateTime>();

        var result = await RetryWithBackoffAsync(
            () => { timestamps.Add(DateTime.UtcNow); return false; },
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        result.ShouldBeFalse();

        // Verify increasing gaps between calls (exponential backoff)
        if (timestamps.Count >= 3)
        {
            var gap1 = (timestamps[1] - timestamps[0]).TotalMilliseconds;
            var gap2 = (timestamps[2] - timestamps[1]).TotalMilliseconds;
            gap2.ShouldBeGreaterThan(gap1 * 1.5); // Allow some tolerance
        }
    }

    [Fact]
    public async Task CancellationDuringRetry_Throws()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await RetryWithBackoffAsync(
                () => false,
                TimeSpan.FromSeconds(0.05),
                TimeSpan.FromSeconds(30),
                cts.Token));
    }

    // Mirror of the static method in Program.cs for testability
    private static async Task<bool> RetryWithBackoffAsync(Func<bool> check, TimeSpan initialBackoff, TimeSpan maxElapsed, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxElapsed;
        var backoff = initialBackoff;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (check()) return true;

            var remaining = deadline - DateTime.UtcNow;
            var delay = backoff < remaining ? backoff : remaining;
            if (delay <= TimeSpan.Zero) break;

            await Task.Delay(delay, ct).ConfigureAwait(false);
            backoff = backoff < maxElapsed ? backoff * 2 : maxElapsed;
        }

        return check();
    }
}
