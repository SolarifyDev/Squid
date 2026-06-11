using Shouldly;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class DiskPressureHealActionTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-heal-test-{Guid.NewGuid():N}");

    public DiskPressureHealActionTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Run_NoPressure_ReportsHealthy_RemovesNothing()
    {
        var removed = new List<string>();
        var candidates = new List<WorkspaceCandidate>
        {
            new(Path.Combine(_workspace, "ws-1"), DateTimeOffset.UtcNow.AddHours(-2), 100, WorkspaceStatus.Succeeded)
        };

        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => candidates,
            policy: new NoPressurePolicy(),                // simulates not-low-pressure
            removeWorkspace: p => removed.Add(p));

        var outcome = await action.RunAsync(CancellationToken.None);

        outcome.Healed.ShouldBeFalse();
        outcome.Message.ShouldBe("healthy");
        removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_WithPolicySelection_RemovesReturnedWorkspaces_AndReportsSummary()
    {
        var removed = new List<string>();
        var ws1 = Path.Combine(_workspace, "old");
        var ws2 = Path.Combine(_workspace, "even-older");
        Directory.CreateDirectory(ws1);
        Directory.CreateDirectory(ws2);

        var candidates = new List<WorkspaceCandidate>
        {
            new(ws1, DateTimeOffset.UtcNow.AddHours(-5), 1024, WorkspaceStatus.Succeeded),
            new(ws2, DateTimeOffset.UtcNow.AddHours(-10), 2048, WorkspaceStatus.Failed)
        };

        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => candidates,
            policy: new AlwaysRemovePolicy(candidates),    // policy returns all
            removeWorkspace: p => removed.Add(p),
            diskProbe: HighPressure);

        var outcome = await action.RunAsync(CancellationToken.None);

        outcome.Healed.ShouldBeTrue();
        removed.ShouldContain(ws1);
        removed.ShouldContain(ws2);
        outcome.Message.ShouldContain("removed 2");
    }

    [Fact]
    public async Task Run_RemoveThrows_OtherCandidatesStillProcessed()
    {
        var removed = new List<string>();
        var ws1 = Path.Combine(_workspace, "a");
        var ws2 = Path.Combine(_workspace, "b");
        Directory.CreateDirectory(ws1);
        Directory.CreateDirectory(ws2);

        var candidates = new List<WorkspaceCandidate>
        {
            new(ws1, DateTimeOffset.UtcNow.AddHours(-5), 100, WorkspaceStatus.Succeeded),
            new(ws2, DateTimeOffset.UtcNow.AddHours(-6), 100, WorkspaceStatus.Succeeded)
        };

        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => candidates,
            policy: new AlwaysRemovePolicy(candidates),
            removeWorkspace: p =>
            {
                if (p == ws1) throw new IOException("simulated disk error");
                removed.Add(p);
            },
            diskProbe: HighPressure);

        var outcome = await action.RunAsync(CancellationToken.None);

        outcome.Healed.ShouldBeTrue();
        removed.ShouldContain(ws2, "a failure on one workspace must not stop the heal sweep");
        removed.ShouldNotContain(ws1);
    }

    [Fact]
    public async Task Run_WorkspaceRootEmpty_ReportsHealthy()
    {
        var removed = new List<string>();
        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => "",
            candidateProbe: _ => Array.Empty<WorkspaceCandidate>(),
            policy: new DefaultWorkspaceCleanupPolicy(),
            removeWorkspace: p => removed.Add(p));

        var outcome = await action.RunAsync(CancellationToken.None);

        outcome.Healed.ShouldBeFalse();
        removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_StillUnderPressureWithNothingEvictable_BacksOff_ThenResetsWhenRelieved()
    {
        // Disk filled by non-workspace usage (or everything protected by retention):
        // the sweep can't help. It must back off (exponentially) instead of churning
        // every CheckInterval, and reset the moment pressure clears.
        var free = 50L;   // start low (5%)
        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => Array.Empty<WorkspaceCandidate>(),   // nothing evictable
            policy: new DefaultWorkspaceCleanupPolicy(),
            removeWorkspace: _ => { },
            checkInterval: TimeSpan.FromMilliseconds(100),
            diskProbe: _ => new DiskPressure(free, 1000));

        action.CheckInterval.ShouldBe(TimeSpan.FromMilliseconds(100), "starts at the base interval");

        await action.RunAsync(CancellationToken.None);
        var afterFirst = action.CheckInterval;
        afterFirst.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(100), "a futile-under-pressure tick backs off");

        await action.RunAsync(CancellationToken.None);
        action.CheckInterval.ShouldBeGreaterThan(afterFirst, "consecutive futile ticks keep backing off");

        // Backoff is bounded — never exceeds the cap (base x 16) no matter how many ticks.
        for (var i = 0; i < 10; i++) await action.RunAsync(CancellationToken.None);
        action.CheckInterval.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(100 * 16));

        free = 900;   // pressure relieved (90% free)
        await action.RunAsync(CancellationToken.None);
        action.CheckInterval.ShouldBe(TimeSpan.FromMilliseconds(100), "the interval resets to base once pressure clears");
    }

    [Fact]
    public async Task Run_RemovedSomeButStillUnderPressure_ReportsBackingOff()
    {
        var ws = Path.Combine(_workspace, "old");
        Directory.CreateDirectory(ws);
        var candidates = new List<WorkspaceCandidate>
        {
            new(ws, DateTimeOffset.UtcNow.AddHours(-50), 100, WorkspaceStatus.Succeeded)
        };

        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => candidates,
            policy: new AlwaysRemovePolicy(candidates),
            removeWorkspace: p => { },
            checkInterval: TimeSpan.FromMilliseconds(100),
            diskProbe: HighPressure);   // re-probe after removal still shows pressure

        var outcome = await action.RunAsync(CancellationToken.None);

        outcome.Healed.ShouldBeTrue();
        outcome.Message.ShouldContain("removed 1", customMessage: "the reclaim summary is preserved");
        outcome.Message.ShouldContain("backing off", customMessage: "and it signals it could not relieve the pressure");
        action.CheckInterval.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    // Injected so CI runners (which have plenty of real free disk) don't cause the
    // action to exit early before exercising the cleanup code paths.
    private static DiskPressure HighPressure(string _) => new(FreeBytes: 50, TotalBytes: 1000);

    // Test-only policies so we don't depend on real disk-space probing here.
    private sealed class NoPressurePolicy : IWorkspaceCleanupPolicy
    {
        public IReadOnlyList<WorkspaceCandidate> SelectForRemoval(IReadOnlyList<WorkspaceCandidate> _, DiskPressure __, RetentionQuota ___)
            => Array.Empty<WorkspaceCandidate>();
    }

    private sealed class AlwaysRemovePolicy : IWorkspaceCleanupPolicy
    {
        private readonly IReadOnlyList<WorkspaceCandidate> _all;
        public AlwaysRemovePolicy(IReadOnlyList<WorkspaceCandidate> all) => _all = all;
        public IReadOnlyList<WorkspaceCandidate> SelectForRemoval(IReadOnlyList<WorkspaceCandidate> _, DiskPressure __, RetentionQuota ___)
            => _all;
    }
}
