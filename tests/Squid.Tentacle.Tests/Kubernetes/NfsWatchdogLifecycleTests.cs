using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Lifecycle;

namespace Squid.Tentacle.Tests.Kubernetes;

[Trait("Category", TentacleTestCategories.Lifecycle)]
public class NfsWatchdogLifecycleTests : TimedTestBase, IDisposable
{
    private readonly string _tempDir;

    public NfsWatchdogLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-nfs-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task BackgroundTask_Starts_And_Cancels_Gracefully()
    {
        var watchdog = new NfsWatchdog(_tempDir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var running = TentacleLifecycleHarness.StartBackgroundTasks(new[] { watchdog }, cts.Token);
        running.Count.ShouldBe(1);

        // Allow at least one health check cycle
        await Task.Delay(200, TestCancellationToken);

        watchdog.IsHealthy.ShouldBeTrue();

        cts.Cancel();

        // Task should complete (either cancelled or completed normally)
        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(3), TestCancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    [Fact]
    public async Task BackgroundTask_SurvivesWorkspaceDeletion_KeepsRunning()
    {
        var watchdog = new NfsWatchdog(_tempDir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var running = TentacleLifecycleHarness.StartBackgroundTasks(new[] { watchdog }, cts.Token);

        // Allow initial health check to succeed
        await Task.Delay(200, TestCancellationToken);
        watchdog.IsHealthy.ShouldBeTrue("Initial health check should pass with valid workspace");

        // Delete workspace to simulate NFS failure
        Directory.Delete(_tempDir, true);

        // The 30s check interval means the watchdog won't detect the failure within 200ms.
        // This test verifies the lifecycle harness keeps the background task alive
        // even if the workspace disappears mid-run.
        await Task.Delay(200, TestCancellationToken);
        running[0].IsCompleted.ShouldBeFalse("Watchdog task should keep running through failures");

        cts.Cancel();

        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(3), TestCancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    [Fact]
    public void ReadinessCheck_ReflectsWatchdogHealth()
    {
        var watchdog = new NfsWatchdog(_tempDir);
        Func<bool> readinessCheck = () => watchdog.IsHealthy;

        readinessCheck().ShouldBeTrue("Initially healthy before any checks");
    }

    public override void Dispose()
    {
        base.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // cleanup best-effort
        }
    }
}
