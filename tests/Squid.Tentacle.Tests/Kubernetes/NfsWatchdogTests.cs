using System;
using System.IO;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class NfsWatchdogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IKubernetesPodOperations> _podOps = new();
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "test-ns" };

    public NfsWatchdogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-nfs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        TentacleMetrics.Reset();
    }

    private NfsWatchdog CreateWatchdog(int forceKillThreshold = 3)
    {
        _settings.NfsWatchdogForceKillThreshold = forceKillThreshold;
        return new NfsWatchdog(_tempDir, _podOps.Object, _settings);
    }

    [Fact]
    public void IsHealthy_InitialState_ReturnsTrue()
    {
        var watchdog = CreateWatchdog();

        watchdog.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public void Name_ReturnsNfsWatchdog()
    {
        var watchdog = CreateWatchdog();

        watchdog.Name.ShouldBe("NfsWatchdog");
    }

    [Fact]
    public async Task RunAsync_CancelledImmediately_CompletesGracefully()
    {
        var watchdog = CreateWatchdog();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await watchdog.RunAsync(cts.Token);
    }

    [Fact]
    public async Task RunAsync_ValidWorkspace_RemainsHealthy()
    {
        var watchdog = CreateWatchdog();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await watchdog.RunAsync(cts.Token);

        watchdog.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_WorkspaceDeleted_BecomesUnhealthy()
    {
        var watchdog = CreateWatchdog();

        Directory.Delete(_tempDir, true);

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await watchdog.RunAsync(cts.Token);

        watchdog.IsHealthy.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WorkspaceRecovered_BecomesHealthyAgain()
    {
        var watchdog = CreateWatchdog();

        Directory.Delete(_tempDir, true);
        var cts1 = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await watchdog.RunAsync(cts1.Token);
        watchdog.IsHealthy.ShouldBeFalse();

        Directory.CreateDirectory(_tempDir);
        var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await watchdog.RunAsync(cts2.Token);
        watchdog.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_CleansSentinelFile_NoLeftoverFiles()
    {
        var watchdog = CreateWatchdog();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await watchdog.RunAsync(cts.Token);

        var sentinelPath = Path.Combine(_tempDir, ".squid-nfs-watchdog");
        File.Exists(sentinelPath).ShouldBeFalse();
    }

    // ========== Force-Kill Recovery ==========

    [Fact]
    public void SingleFailure_DoesNotDeletePod()
    {
        var watchdog = CreateWatchdog(forceKillThreshold: 3);

        Directory.Delete(_tempDir, true);
        watchdog.CheckWorkspaceHealth();

        watchdog.ConsecutiveFailures.ShouldBe(1);
        _podOps.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void ConsecutiveFailuresBelowThreshold_DoesNotDeletePod()
    {
        var watchdog = CreateWatchdog(forceKillThreshold: 3);

        Directory.Delete(_tempDir, true);
        watchdog.CheckWorkspaceHealth();
        watchdog.CheckWorkspaceHealth();

        watchdog.ConsecutiveFailures.ShouldBe(2);
        _podOps.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void ConsecutiveFailuresReachThreshold_DeletesPod()
    {
        var watchdog = CreateWatchdog(forceKillThreshold: 3);

        Directory.Delete(_tempDir, true);
        watchdog.CheckWorkspaceHealth();
        watchdog.CheckWorkspaceHealth();
        watchdog.CheckWorkspaceHealth();

        _podOps.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public void FailureThenRecovery_ResetsCounter()
    {
        var watchdog = CreateWatchdog(forceKillThreshold: 3);

        Directory.Delete(_tempDir, true);
        watchdog.CheckWorkspaceHealth();
        watchdog.CheckWorkspaceHealth();
        watchdog.ConsecutiveFailures.ShouldBe(2);

        Directory.CreateDirectory(_tempDir);
        watchdog.CheckWorkspaceHealth();
        watchdog.ConsecutiveFailures.ShouldBe(0);

        _podOps.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void ForceKill_IncrementsMetric()
    {
        var watchdog = CreateWatchdog(forceKillThreshold: 1);

        Directory.Delete(_tempDir, true);
        watchdog.CheckWorkspaceHealth();

        TentacleMetrics.NfsForceKillsTotal.ShouldBe(1);
    }

    [Fact]
    public void ForceDeleteSelf_UsesConfiguredGracePeriod()
    {
        _settings.NfsForceKillGracePeriodSeconds = 45;
        var watchdog = CreateWatchdog(forceKillThreshold: 1);

        Directory.Delete(_tempDir, true);
        watchdog.CheckWorkspaceHealth();

        _podOps.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", 45), Times.Once);
    }

    public void Dispose()
    {
        TentacleMetrics.Reset();

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
