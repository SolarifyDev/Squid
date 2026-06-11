using Shouldly;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

/// <summary>
/// 🟢 HIGH-fidelity end-to-end coverage of the WIRED disk self-heal chain: real
/// <see cref="WorkspaceProbe"/> + real <see cref="DefaultWorkspaceCleanupPolicy"/>
/// (with the live retention + fresh-grace floors) + real
/// <see cref="ScriptStateStore"/> state files + real recursive
/// <see cref="Directory.Delete(string, bool)"/> against real OS directories. Only
/// the disk-pressure MEASUREMENT is injected (forced low/critical) — the same
/// substitution the unit tests use, because CI runners have plenty of free disk and
/// would otherwise no-op the sweep.
///
/// <para>The unit tests cover each component in isolation; this proves the
/// composed production path (the one <c>SelfHealBackgroundTask.ForLocalWorkspaces</c>
/// builds) actually reclaims a real completed workspace while a real Running-state
/// workspace survives, honours the operator's retention TTL under mild pressure,
/// and reclaims a corrupt-state workspace under critical pressure (the finding that
/// a corrupt state file would otherwise be un-reclaimable forever).</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Integration)]
public sealed class DiskSelfHealWiredE2ETests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-selfheal-e2e-{Guid.NewGuid():N}");
    private readonly ScriptStateStoreFactory _factory = new();

    public DiskSelfHealWiredE2ETests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task WiredChain_CriticalPressure_ReclaimsOldSucceeded_KeepsRunningAndRecent()
    {
        var oldSucceeded = StageWorkspace("old-ok", Complete(0), age: TimeSpan.FromHours(50));
        var recentSucceeded = StageWorkspace("recent-ok", Complete(0), age: TimeSpan.FromHours(1));
        var running = StageWorkspace("live", Running(), age: TimeSpan.FromHours(2));

        var action = BuildWiredAction(Critical, keepSucceeded: 1, keepFailed: 1);

        var outcome = await action.RunAsync(CancellationToken.None);

        outcome.Healed.ShouldBeTrue();
        Directory.Exists(oldSucceeded).ShouldBeFalse("an old succeeded workspace beyond the keep-set is reclaimed under critical pressure");
        Directory.Exists(recentSucceeded).ShouldBeTrue("the newest succeeded workspace is kept by the retention quota");
        Directory.Exists(running).ShouldBeTrue("a Running-state workspace is Active and must NEVER be deleted out from under a live script");
    }

    [Fact]
    public async Task WiredChain_NonCriticalPressure_HonoursRetentionTtl()
    {
        var withinTtl = StageWorkspace("within-ttl", Complete(0), age: TimeSpan.FromHours(5));
        var beyondTtl = StageWorkspace("beyond-ttl", Complete(0), age: TimeSpan.FromHours(50));

        // keep 0 so neither sits in the keep-set — only the retention TTL decides.
        var action = BuildWiredAction(NonCriticalLow, keepSucceeded: 0, keepFailed: 0);

        await action.RunAsync(CancellationToken.None);

        Directory.Exists(withinTtl).ShouldBeTrue("under mild pressure a workspace inside the operator's orphan TTL is preserved for post-mortem");
        Directory.Exists(beyondTtl).ShouldBeFalse("a workspace older than the TTL is reclaimed");
    }

    [Fact]
    public async Task WiredChain_CriticalPressure_ReclaimsCorruptStateWorkspace()
    {
        // A dead workspace whose state file is corrupt (partial write when the disk
        // filled mid-Save) must still be reclaimable — classified Unknown, not skipped
        // forever. This is the exact scenario disk pressure produces.
        var corrupt = StageWorkspace("corrupt", state: null, age: TimeSpan.FromHours(50));
        File.WriteAllText(Path.Combine(corrupt, "scriptstate.json"), "{ partial-write garbage");
        Directory.SetLastWriteTimeUtc(corrupt, DateTime.UtcNow - TimeSpan.FromHours(50));

        var action = BuildWiredAction(Critical, keepSucceeded: 0, keepFailed: 0);

        await action.RunAsync(CancellationToken.None);

        Directory.Exists(corrupt).ShouldBeFalse("a corrupt-state dead workspace must be reclaimable under critical pressure, not un-reclaimable forever");
    }

    // ── Helpers ──

    private static DiskPressure Critical(string _) => new(FreeBytes: 50, TotalBytes: 1000);          // 5% — critical
    private static DiskPressure NonCriticalLow(string _) => new(FreeBytes: 150, TotalBytes: 1000);   // 15% — low, not critical

    private DiskPressureHealAction BuildWiredAction(Func<string, DiskPressure> diskProbe, int keepSucceeded, int keepFailed)
        => new(
            workspaceRootProvider: () => _root,
            candidateProbe: root => WorkspaceProbe.Probe(root, _factory),
            policy: new DefaultWorkspaceCleanupPolicy(
                minRetentionAge: TimeSpan.FromHours(24),
                freshGraceWindow: SelfHealOptions.FreshWorkspaceGraceWindow),
            removeWorkspace: path => Directory.Delete(path, recursive: true),
            quota: new RetentionQuota(keepSucceeded, keepFailed),
            diskProbe: diskProbe);

    private string StageWorkspace(string ticketId, ScriptState state, TimeSpan age)
    {
        var dir = Path.Combine(_root, $"squid-tentacle-{ticketId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        File.WriteAllBytes(Path.Combine(dir, "output.log"), new byte[4096]);

        if (state != null)
            _factory.Create(dir).Save(state);

        // Set the dir mtime LAST — Save() touches the directory, so the age must be
        // stamped after every write to reflect the intended workspace age.
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);

        return dir;
    }

    private static ScriptState Complete(int exitCode) => new()
    {
        TicketId = "t",
        Progress = ScriptProgress.Complete,
        ExitCode = exitCode,
        CreatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow
    };

    private static ScriptState Running() => new()
    {
        TicketId = "t",
        Progress = ScriptProgress.Running,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
