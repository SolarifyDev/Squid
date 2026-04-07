using System.IO;
using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshRetryHelperTests
{
    [Fact]
    public void ExecuteWithRetry_SucceedsFirstAttempt_ReturnsResult()
    {
        var callCount = 0;

        var result = SshRetryHelper.ExecuteWithRetry(() => { callCount++; return 42; }, _ => true);

        result.ShouldBe(42);
        callCount.ShouldBe(1);
    }

    [Fact]
    public void ExecuteWithRetry_TransientThenSuccess_Retries()
    {
        var callCount = 0;

        var result = SshRetryHelper.ExecuteWithRetry(() =>
        {
            callCount++;
            if (callCount < 3) throw new IOException("transient");
            return 99;
        }, ex => ex is IOException, maxAttempts: 3);

        result.ShouldBe(99);
        callCount.ShouldBe(3);
    }

    [Fact]
    public void ExecuteWithRetry_NonTransient_ThrowsImmediately()
    {
        var callCount = 0;

        Should.Throw<ArgumentException>(() =>
            SshRetryHelper.ExecuteWithRetry<int>(() =>
            {
                callCount++;
                throw new ArgumentException("permanent");
            }, _ => false, maxAttempts: 3));

        callCount.ShouldBe(1);
    }

    [Fact]
    public void ExecuteWithRetry_ExhaustsAttempts_ThrowsLastException()
    {
        var callCount = 0;

        var ex = Should.Throw<IOException>(() =>
            SshRetryHelper.ExecuteWithRetry<int>(() =>
            {
                callCount++;
                throw new IOException($"attempt {callCount}");
            }, _ => true, maxAttempts: 3));

        callCount.ShouldBe(3);
        ex.Message.ShouldBe("attempt 3");
    }

    [Fact]
    public void ExecuteWithRetry_ActionOverload_Works()
    {
        var callCount = 0;

        SshRetryHelper.ExecuteWithRetry(() => { callCount++; }, _ => true);

        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SucceedsFirstAttempt_ReturnsResult()
    {
        var result = await SshRetryHelper.ExecuteWithRetryAsync(() => Task.FromResult(42), _ => true);

        result.ShouldBe(42);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_TransientThenSuccess_Retries()
    {
        var callCount = 0;

        var result = await SshRetryHelper.ExecuteWithRetryAsync(() =>
        {
            callCount++;
            if (callCount < 2) throw new IOException("transient");
            return Task.FromResult(99);
        }, ex => ex is IOException, maxAttempts: 3);

        result.ShouldBe(99);
        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_CancellationDuringDelay_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            SshRetryHelper.ExecuteWithRetryAsync<int>(() => throw new IOException("transient"),
                _ => true, maxAttempts: 3, ct: cts.Token));
    }

    [Fact]
    public void NextDelay_DoublesEachTime()
    {
        var d1 = SshRetryHelper.NextDelay(TimeSpan.FromSeconds(1));
        d1.ShouldBe(TimeSpan.FromSeconds(2));

        var d2 = SshRetryHelper.NextDelay(d1);
        d2.ShouldBe(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void NextDelay_CapsAtMaxDelay()
    {
        var d = SshRetryHelper.NextDelay(TimeSpan.FromSeconds(10));

        d.ShouldBeLessThanOrEqualTo(SshRetryHelper.DefaultMaxDelay);
    }
}
