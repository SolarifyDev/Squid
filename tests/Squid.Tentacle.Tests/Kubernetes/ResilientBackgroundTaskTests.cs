using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class ResilientBackgroundTaskTests
{
    [Fact]
    public async Task RunAsync_InnerRunsNormally_CompletesWithoutRestart()
    {
        var runCount = 0;
        var inner = CreateTask("test", ct => { runCount++; return Task.CompletedTask; });
        var resilient = new ResilientBackgroundTask(inner);

        await resilient.RunAsync(CancellationToken.None);

        runCount.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_InnerThrows_RestartsWithBackoff()
    {
        var runCount = 0;
        var inner = CreateTask("test", ct =>
        {
            runCount++;
            if (runCount == 1)
                throw new InvalidOperationException("crash");
            return Task.CompletedTask;
        });
        var resilient = new ResilientBackgroundTask(inner);

        await resilient.RunAsync(CancellationToken.None);

        runCount.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_MultipleFailures_BackoffGrowsExponentially()
    {
        var attempts = new List<DateTimeOffset>();
        var runCount = 0;
        var cts = new CancellationTokenSource();

        var inner = CreateTask("test", ct =>
        {
            runCount++;
            attempts.Add(DateTimeOffset.UtcNow);
            if (runCount >= 4)
                cts.Cancel();
            throw new InvalidOperationException("crash");
        });
        var resilient = new ResilientBackgroundTask(inner);

        await resilient.RunAsync(cts.Token);

        runCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringBackoff_ExitsGracefully()
    {
        var cts = new CancellationTokenSource();
        var inner = CreateTask("test", ct =>
        {
            Task.Run(() =>
            {
                Thread.Sleep(100);
                cts.Cancel();
            });
            throw new InvalidOperationException("crash");
        });
        var resilient = new ResilientBackgroundTask(inner);

        await Should.NotThrowAsync(() => resilient.RunAsync(cts.Token));
    }

    [Fact]
    public async Task RunAsync_InnerRecovery_ExitsOnNormalCompletion()
    {
        var runCount = 0;
        var inner = CreateTask("test", ct =>
        {
            runCount++;
            if (runCount <= 2)
                throw new InvalidOperationException("transient crash");
            return Task.CompletedTask;
        });
        var resilient = new ResilientBackgroundTask(inner);

        await resilient.RunAsync(CancellationToken.None);

        runCount.ShouldBe(3);
    }

    [Fact]
    public void CalculateBackoff_FirstFailure_Returns1s()
    {
        var backoff = ResilientBackgroundTask.CalculateBackoff(1);

        backoff.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void CalculateBackoff_SixthFailure_Returns32s()
    {
        var backoff = ResilientBackgroundTask.CalculateBackoff(6);

        backoff.ShouldBe(TimeSpan.FromSeconds(32));
    }

    [Fact]
    public void CalculateBackoff_LargeFailureCount_CapsAt60s()
    {
        var backoff = ResilientBackgroundTask.CalculateBackoff(100);

        backoff.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Name_DelegatesFromInner()
    {
        var inner = CreateTask("MyTestTask", _ => Task.CompletedTask);
        var resilient = new ResilientBackgroundTask(inner);

        resilient.Name.ShouldBe("MyTestTask");
    }

    private static ITentacleBackgroundTask CreateTask(string name, Func<CancellationToken, Task> runAction)
    {
        var mock = new Mock<ITentacleBackgroundTask>();
        mock.Setup(m => m.Name).Returns(name);
        mock.Setup(m => m.RunAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => runAction(ct));
        return mock.Object;
    }
}
