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
            removeWorkspace: p => removed.Add(p));

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
            });

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
