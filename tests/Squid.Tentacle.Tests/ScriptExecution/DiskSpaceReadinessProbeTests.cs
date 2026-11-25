using Squid.Tentacle.Flavors.KubernetesAgent;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class DiskSpaceReadinessProbeTests
{
    [Fact]
    public void IsReady_CheckPasses_ReturnsTrue()
    {
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromSeconds(5), _ => true);

        probe.IsReady().ShouldBeTrue();
    }

    [Fact]
    public void IsReady_CheckFails_ReturnsFalse()
    {
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromSeconds(5), _ => false);

        probe.IsReady().ShouldBeFalse();
    }

    [Fact]
    public void IsReady_CheckThrows_ReturnsFalseInsteadOfThrowing()
    {
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromSeconds(5), _ => throw new IOException("boom"));

        probe.IsReady().ShouldBeFalse();
    }

    [Fact]
    public void IsReady_HungCheck_ReturnsNotReadyWithinTheTimeout_InsteadOfHanging()
    {
        using var release = new ManualResetEventSlim(false);
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromMilliseconds(200), _ => { release.Wait(); return true; });

        try
        {
            var started = DateTime.UtcNow;

            probe.IsReady().ShouldBeFalse();

            (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void IsReady_WhileAPreviousCheckIsStillStuck_DoesNotStartAnotherThreadPerProbe()
    {
        using var release = new ManualResetEventSlim(false);
        var started = 0;
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromMilliseconds(100), _ => { Interlocked.Increment(ref started); release.Wait(); return true; });

        try
        {
            for (var i = 0; i < 10; i++) probe.IsReady().ShouldBeFalse();

            started.ShouldBe(1, "a hung hard-mounted volume must strand ONE thread, not one per probe");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task IsReady_OverlappingProbesDuringAHealthyInFlightCheck_ShareItsResult_InsteadOfReportingNotReady()
    {
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromSeconds(10), _ => { Interlocked.Increment(ref calls); release.Wait(); return true; });

        var first = Task.Run(probe.IsReady);

        SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5)).ShouldBeTrue("the first check must be in flight");

        var overlapping = Enumerable.Range(0, 4).Select(_ => Task.Run(probe.IsReady)).ToArray();

        await Task.Delay(200);
        release.Set();

        (await first.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();

        foreach (var probeResult in overlapping)
            (await probeResult.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue("a healthy check that is merely still running must not flap readiness for the other probes");

        calls.ShouldBe(1, "overlapping probes share the one in-flight check");
    }

    [Fact]
    public async Task IsReady_AJoinerWaitsOnlyForTheRemainingBudgetOfTheInFlightCheck_NotAFullTimeoutOfItsOwn()
    {
        using var release = new ManualResetEventSlim(false);
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromSeconds(3), _ => { release.Wait(); return true; });

        try
        {
            var first = Task.Run(probe.IsReady);

            await Task.Delay(1500);

            var started = DateTime.UtcNow;

            probe.IsReady().ShouldBeFalse();

            (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromMilliseconds(2400), "the check has already used half its budget; a joiner may not wait a fresh full timeout");
            (await first.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeFalse();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void IsReady_RecoversOnceTheStuckCheckCompletes()
    {
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var probe = new DiskSpaceReadinessProbe("/ws", TimeSpan.FromMilliseconds(100), _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) release.Wait();

            return true;
        });

        probe.IsReady().ShouldBeFalse();

        release.Set();
        SpinWait.SpinUntil(() => probe.IsReady(), TimeSpan.FromSeconds(5)).ShouldBeTrue("the probe must report ready again after the volume recovers");
    }

    [Fact]
    public void AddWorkspaceReadinessChecks_NfsVerdictRunsBeforeTheDiskCheck_SoAnUnhealthyMountIsNeverStatfd()
    {
        var diskCalls = 0;
        var checks = new List<Func<bool>>();

        KubernetesAgentFlavor.AddWorkspaceReadinessChecks(checks, () => false, () => { diskCalls++; return true; });

        checks.All(check => check()).ShouldBeFalse();
        diskCalls.ShouldBe(0, "the disk statfs must not run when the NFS watchdog already reports unhealthy");
    }

    [Fact]
    public void AddWorkspaceReadinessChecks_WithoutInProcessWatchdog_StillChecksDisk()
    {
        var diskCalls = 0;
        var checks = new List<Func<bool>>();

        KubernetesAgentFlavor.AddWorkspaceReadinessChecks(checks, null, () => { diskCalls++; return true; });

        checks.All(check => check()).ShouldBeTrue();
        diskCalls.ShouldBe(1);
    }

    [Fact]
    public void KubernetesAgentFlavor_WiresReadinessThroughTheTimeoutProbe_NotTheBlockingCheck()
    {
        // Wiring guard: deleting the call site must fail a test (readiness on a hung NFS is the bug).
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Squid.Tentacle", "Flavors", "KubernetesAgent", "KubernetesAgentFlavor.cs"));

        source.ShouldContain("new DiskSpaceReadinessProbe(");
        source.ShouldContain("AddWorkspaceReadinessChecks(readinessChecks, nfsHealthy, diskProbe.IsReady)");
        source.ShouldNotContain("DiskSpaceChecker.HasSufficientSpace(");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid repo working tree");
    }
}
