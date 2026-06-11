using Shouldly;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class WorkspaceCleanupPolicyTests
{
    private readonly DefaultWorkspaceCleanupPolicy _policy = new();

    [Fact]
    public void NoPressure_SelectsNothing()
    {
        var pressure = new DiskPressure(FreeBytes: 800, TotalBytes: 1000);   // 80% free
        var candidates = Candidates(("a", ago: 10, status: WorkspaceStatus.Succeeded));

        var selected = _policy.SelectForRemoval(candidates, pressure, RetentionQuota.Default);

        selected.ShouldBeEmpty();
    }

    [Fact]
    public void UnderPressure_KeepsRetentionQuota_OfSucceeded()
    {
        var pressure = new DiskPressure(FreeBytes: 50, TotalBytes: 1000);    // 5% — critical
        var quota = new RetentionQuota(KeepLatestSucceeded: 2, KeepLatestFailed: 0);

        var candidates = Candidates(
            ("a", ago: 100, status: WorkspaceStatus.Succeeded),
            ("b", ago: 90, status: WorkspaceStatus.Succeeded),
            ("c", ago: 80, status: WorkspaceStatus.Succeeded),
            ("d", ago: 70, status: WorkspaceStatus.Succeeded));

        var selected = _policy.SelectForRemoval(candidates, pressure, quota);

        var paths = selected.Select(s => s.Path).ToList();
        paths.ShouldContain("a");
        paths.ShouldContain("b");
        paths.ShouldNotContain("c", "the 2 newest succeeded must be kept");
        paths.ShouldNotContain("d");
    }

    [Fact]
    public void NeverSelectsActiveWorkspaces()
    {
        var pressure = new DiskPressure(FreeBytes: 50, TotalBytes: 1000);
        var candidates = Candidates(
            ("running", ago: 1, status: WorkspaceStatus.Active),
            ("old-done", ago: 999, status: WorkspaceStatus.Succeeded));

        var selected = _policy.SelectForRemoval(candidates, pressure, RetentionQuota.Default);

        selected.Select(s => s.Path).ShouldNotContain("running",
            "active workspace is running a live script and must never be removed");
    }

    [Fact]
    public void UnderPressure_KeepsRetentionQuota_OfFailed()
    {
        var pressure = new DiskPressure(FreeBytes: 50, TotalBytes: 1000);
        var quota = new RetentionQuota(KeepLatestSucceeded: 0, KeepLatestFailed: 3);

        var candidates = Candidates(
            ("f1", ago: 100, status: WorkspaceStatus.Failed),
            ("f2", ago: 90, status: WorkspaceStatus.Failed),
            ("f3", ago: 80, status: WorkspaceStatus.Failed),
            ("f4", ago: 70, status: WorkspaceStatus.Failed));

        var selected = _policy.SelectForRemoval(candidates, pressure, quota);

        var paths = selected.Select(s => s.Path).ToList();
        paths.ShouldContain("f1", "oldest failures removed when 4th keeps latest 3");
        paths.ShouldNotContain("f2");
        paths.ShouldNotContain("f3");
        paths.ShouldNotContain("f4");
    }

    [Fact]
    public void StopsRemoving_OnceTargetFreePercentageReached()
    {
        // Want 20% free. Currently 5% free (50/1000). Need +150 bytes reclaimed.
        var pressure = new DiskPressure(FreeBytes: 50, TotalBytes: 1000);
        var quota = new RetentionQuota(0, 0);

        var candidates = Candidates(
            ("huge", ago: 100, status: WorkspaceStatus.Succeeded, sizeBytes: 500),
            ("small", ago: 90, status: WorkspaceStatus.Succeeded, sizeBytes: 10));

        var selected = _policy.SelectForRemoval(candidates, pressure, quota);

        selected.Select(s => s.Path).ShouldContain("huge");
        selected.Select(s => s.Path).ShouldNotContain("small",
            "once the critical-mode 30% target is reached via the huge workspace, no more candidates are selected");
    }

    [Fact]
    public void NullCandidates_ReturnsEmpty()
    {
        var pressure = new DiskPressure(FreeBytes: 50, TotalBytes: 1000);

        _policy.SelectForRemoval(null, pressure, RetentionQuota.Default).ShouldBeEmpty();
    }

    [Fact]
    public void RetentionFloor_NonCriticalPressure_KeepsWorkspacesWithinTtl_EvictsOlder()
    {
        // The new disk sweep must NOT silently override the operator's orphan-workspace
        // TTL (post-mortem window). Under mild (non-critical) pressure, a completed
        // workspace younger than minRetentionAge is protected even though it is beyond
        // the keep-set; only those older than the TTL are reclaimed.
        var now = DateTimeOffset.UtcNow;
        var policy = new DefaultWorkspaceCleanupPolicy(minRetentionAge: TimeSpan.FromHours(24), clock: () => now);

        var pressure = new DiskPressure(FreeBytes: 150, TotalBytes: 1000);   // 15% — low but NOT critical
        pressure.IsLow.ShouldBeTrue();
        pressure.IsCritical.ShouldBeFalse();

        var candidates = new[]
        {
            new WorkspaceCandidate("within-ttl", now.AddHours(-5), 500, WorkspaceStatus.Succeeded),
            new WorkspaceCandidate("beyond-ttl", now.AddHours(-48), 500, WorkspaceStatus.Succeeded)
        };

        var selected = _policy.SelectForRemoval(candidates, pressure, new RetentionQuota(0, 0));

        var paths = selected.Select(s => s.Path).ToList();
        paths.ShouldContain("beyond-ttl", "a workspace older than the operator's TTL is reclaimable under pressure");
        paths.ShouldNotContain("within-ttl", "a workspace inside the operator's post-mortem TTL must NOT be evicted under mild pressure");
    }

    [Fact]
    public void RetentionFloor_CriticalPressure_BypassesTtl_ButFreshGraceStillProtects()
    {
        // Under CRITICAL pressure the retention TTL is bypassed (reclaiming disk wins),
        // BUT the short fresh-grace floor still protects a just-created workspace — that
        // is the TOCTOU guard for a deployment initialising a brand-new workspace, and
        // it must hold even in an emergency.
        var now = DateTimeOffset.UtcNow;
        var policy = new DefaultWorkspaceCleanupPolicy(
            minRetentionAge: TimeSpan.FromHours(24),
            freshGraceWindow: TimeSpan.FromSeconds(60),
            clock: () => now);

        var pressure = new DiskPressure(FreeBytes: 50, TotalBytes: 1000);   // 5% — critical
        pressure.IsCritical.ShouldBeTrue();

        var candidates = new[]
        {
            new WorkspaceCandidate("recent-within-ttl", now.AddHours(-1), 500, WorkspaceStatus.Succeeded),
            new WorkspaceCandidate("just-created", now.AddSeconds(-10), 500, WorkspaceStatus.Unknown)
        };

        var selected = _policy.SelectForRemoval(candidates, pressure, new RetentionQuota(0, 0));

        var paths = selected.Select(s => s.Path).ToList();
        paths.ShouldContain("recent-within-ttl", "critical pressure bypasses the retention TTL to reclaim disk");
        paths.ShouldNotContain("just-created", "the fresh-grace floor protects a just-created workspace even under critical pressure (TOCTOU guard)");
    }

    [Fact]
    public void CriticalPressure_RaisesTargetTo30Percent()
    {
        // 5% free — strictly critical. Need more than one workspace removed to reach 30%.
        var pressure = new DiskPressure(FreeBytes: 50, TotalBytes: 1000);
        pressure.IsCritical.ShouldBeTrue();

        var quota = new RetentionQuota(0, 0);

        var candidates = Candidates(
            ("a", ago: 100, status: WorkspaceStatus.Succeeded, sizeBytes: 150),
            ("b", ago: 90, status: WorkspaceStatus.Succeeded, sizeBytes: 150));

        var selected = _policy.SelectForRemoval(candidates, pressure, quota);

        selected.Count.ShouldBeGreaterThanOrEqualTo(2,
            "under critical pressure the policy reclaims to 30% free — one 150-byte workspace is not enough to close the gap (50→300 bytes of headroom)");
    }

    private static IReadOnlyList<WorkspaceCandidate> Candidates(params (string Path, int AgoSeconds, WorkspaceStatus Status, long SizeBytes)[] rows)
    {
        var now = DateTimeOffset.UtcNow;
        return rows.Select(r => new WorkspaceCandidate(r.Path, now.AddSeconds(-r.AgoSeconds), r.SizeBytes, r.Status)).ToList();
    }

    private static IReadOnlyList<WorkspaceCandidate> Candidates(params (string Path, int ago, WorkspaceStatus status)[] rows)
        => Candidates(rows.Select(r => (r.Path, r.ago, r.status, SizeBytes: 100L)).ToArray());
}
