using System;
using System.IO;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class NfsWatchdogTests : IDisposable
{
    private readonly string _tempDir;

    public NfsWatchdogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-nfs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void IsHealthy_InitialState_ReturnsTrue()
    {
        var watchdog = new NfsWatchdog(_tempDir);

        watchdog.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public void Name_ReturnsNfsWatchdog()
    {
        var watchdog = new NfsWatchdog(_tempDir);

        watchdog.Name.ShouldBe("NfsWatchdog");
    }

    [Fact]
    public async Task RunAsync_CancelledImmediately_CompletesGracefully()
    {
        var watchdog = new NfsWatchdog(_tempDir);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should not throw
        await watchdog.RunAsync(cts.Token);
    }

    [Fact]
    public async Task RunAsync_ValidWorkspace_RemainsHealthy()
    {
        var watchdog = new NfsWatchdog(_tempDir);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await watchdog.RunAsync(cts.Token);

        watchdog.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_WorkspaceDeleted_BecomesUnhealthy()
    {
        var watchdog = new NfsWatchdog(_tempDir);

        // Delete the workspace to simulate NFS failure
        Directory.Delete(_tempDir, true);

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await watchdog.RunAsync(cts.Token);

        watchdog.IsHealthy.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WorkspaceRecovered_BecomesHealthyAgain()
    {
        var watchdog = new NfsWatchdog(_tempDir);

        // Delete to make unhealthy
        Directory.Delete(_tempDir, true);
        var cts1 = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await watchdog.RunAsync(cts1.Token);
        watchdog.IsHealthy.ShouldBeFalse();

        // Recreate to recover
        Directory.CreateDirectory(_tempDir);
        var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await watchdog.RunAsync(cts2.Token);
        watchdog.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_CleansSentinelFile_NoLeftoverFiles()
    {
        var watchdog = new NfsWatchdog(_tempDir);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await watchdog.RunAsync(cts.Token);

        var sentinelPath = Path.Combine(_tempDir, ".squid-nfs-watchdog");
        File.Exists(sentinelPath).ShouldBeFalse();
    }

    public void Dispose()
    {
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
