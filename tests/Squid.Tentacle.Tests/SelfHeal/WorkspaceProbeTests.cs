using Shouldly;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

/// <summary>
/// Pins <see cref="WorkspaceProbe"/> — the enumeration + status classification
/// that turns on-disk script workspaces into <see cref="WorkspaceCandidate"/>s for
/// the disk-pressure heal sweep. This is the wiring glue between the persisted
/// <see cref="ScriptState"/> (Progress + ExitCode) and the cleanup policy; the
/// status mapping decides what the per-status retention windows protect.
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class WorkspaceProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-probe-test-{Guid.NewGuid():N}");
    private readonly ScriptStateStoreFactory _factory = new();

    public WorkspaceProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private string MakeWorkspace(string ticketId, ScriptState? state = null, string? content = null)
    {
        var dir = Path.Combine(_root, $"squid-tentacle-{ticketId}");
        Directory.CreateDirectory(dir);

        if (content != null)
            File.WriteAllText(Path.Combine(dir, "output.log"), content);

        if (state != null)
            _factory.Create(dir).Save(state);

        return dir;
    }

    private static ScriptState Complete(string ticketId, int exitCode) => new()
    {
        TicketId = ticketId,
        Progress = ScriptProgress.Complete,
        ExitCode = exitCode,
        CreatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow
    };

    private static ScriptState Running(string ticketId) => new()
    {
        TicketId = ticketId,
        Progress = ScriptProgress.Running,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Probe_CompleteExitZero_IsSucceeded()
    {
        MakeWorkspace("ok", Complete("ok", exitCode: 0));

        var c = WorkspaceProbe.Probe(_root, _factory).ShouldHaveSingleItem();

        c.Status.ShouldBe(WorkspaceStatus.Succeeded);
    }

    [Fact]
    public void Probe_CompleteNonZero_IsFailed()
    {
        MakeWorkspace("bad", Complete("bad", exitCode: 1));

        WorkspaceProbe.Probe(_root, _factory).ShouldHaveSingleItem()
            .Status.ShouldBe(WorkspaceStatus.Failed);
    }

    [Fact]
    public void Probe_RunningScript_IsActive_SoThePolicyNeverEvictsIt()
    {
        MakeWorkspace("live", Running("live"));

        WorkspaceProbe.Probe(_root, _factory).ShouldHaveSingleItem()
            .Status.ShouldBe(WorkspaceStatus.Active);
    }

    [Fact]
    public void Probe_NoStateFile_IsUnknown()
    {
        MakeWorkspace("orphan");   // dir exists, no .squid-state.json

        WorkspaceProbe.Probe(_root, _factory).ShouldHaveSingleItem()
            .Status.ShouldBe(WorkspaceStatus.Unknown);
    }

    [Fact]
    public void Probe_StateFileExistsButCorrupt_ClassifiedUnknown_SweepContinues()
    {
        // A present-but-unreadable state file (corrupt primary, no usable backup —
        // e.g. a partial write when the disk filled mid-Save) makes ScriptStateStore
        // Exists()==true but Load() throw. The probe must classify it Unknown
        // (reclaimable under pressure) — NOT let the throw drop it from the candidate
        // list forever (which would make a dead-but-corrupt workspace permanently
        // un-reclaimable, defeating the heal precisely when disk is full) and NOT
        // abort the whole sweep so the valid workspace beside it is still classified.
        var corrupt = MakeWorkspace("corrupt");
        File.WriteAllText(Path.Combine(corrupt, "scriptstate.json"), "{ this is no longer valid json");
        MakeWorkspace("good", Complete("good", 0));

        var byStatus = WorkspaceProbe.Probe(_root, _factory).ToLookup(c => c.Status);

        byStatus[WorkspaceStatus.Unknown].ShouldHaveSingleItem()
            .Path.ShouldEndWith("squid-tentacle-corrupt");
        byStatus[WorkspaceStatus.Succeeded].ShouldHaveSingleItem()
            .Path.ShouldEndWith("squid-tentacle-good");
    }

    [Fact]
    public void Probe_IgnoresDirectoriesNotMatchingTheWorkspacePrefix()
    {
        Directory.CreateDirectory(Path.Combine(_root, "some-other-dir"));
        Directory.CreateDirectory(Path.Combine(_root, "calamari-cache"));
        MakeWorkspace("real", Complete("real", 0));

        var candidates = WorkspaceProbe.Probe(_root, _factory);

        candidates.ShouldHaveSingleItem()
            .Path.ShouldEndWith("squid-tentacle-real");
    }

    [Fact]
    public void Probe_MeasuresWorkspaceSize_RecursivelyAcrossNestedDirs()
    {
        // Real script workspaces nest artefacts (extracted packages, Calamari trees),
        // so the recursive walk is the load-bearing part: the policy reclaims by
        // SizeBytes, and a TopDirectoryOnly regression would silently halve the
        // measured size for deep workspaces and starve the heal. Put payload at TWO
        // depths and assert the summed size, pinning recursion AND per-file accrual.
        var dir = MakeWorkspace("sized", Complete("sized", 0));
        File.WriteAllBytes(Path.Combine(dir, "top.bin"), new byte[4096]);
        var deep = Path.Combine(dir, "sub", "deeper");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "nested.bin"), new byte[8192]);

        var c = WorkspaceProbe.Probe(_root, _factory).ShouldHaveSingleItem();

        c.SizeBytes.ShouldBeGreaterThanOrEqualTo(4096 + 8192,
            customMessage: "Size must recurse into nested dirs (nested.bin under sub/deeper) AND sum every file " +
                          "(top.bin) — a TopDirectoryOnly walk would miss the 8 KB nested file.");
    }

    [Fact]
    public void Probe_SizeWalk_DoesNotFollowDirectorySymlinks()
    {
        // A deployment package (or a runtime `ln -s`) can plant a directory symlink
        // inside the workspace. The default AllDirectories walk follows it — a
        // symlink-to-parent loops until the path-length limit (CPU/IO burn on the
        // exact low-disk tick the heal targets) and a symlink to /var or C:\ mis-
        // attributes unrelated disk to the workspace. The walk must skip reparse points.
        var dir = MakeWorkspace("withsymlink", Complete("withsymlink", 0));
        File.WriteAllBytes(Path.Combine(dir, "real.bin"), new byte[4096]);

        try
        {
            // Self-referential loop: {dir}/loop -> {dir}. A followed walk would recurse forever.
            Directory.CreateSymbolicLink(Path.Combine(dir, "loop"), dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;   // host doesn't permit symlink creation (e.g. Windows without dev mode) — skip
        }

        var c = WorkspaceProbe.Probe(_root, _factory).ShouldHaveSingleItem();

        // Completes (no hang) AND counts only the real file + state json — the symlink
        // loop contributes nothing. If reparse points were followed this would either
        // hang the test or balloon the size.
        c.SizeBytes.ShouldBeGreaterThanOrEqualTo(4096);
        c.SizeBytes.ShouldBeLessThan(4096 * 4,
            customMessage: "Size must exclude the symlink target — a followed loop would re-count files unboundedly.");
    }

    [Fact]
    public void Probe_MissingRoot_ReturnsEmpty()
        => WorkspaceProbe.Probe(Path.Combine(_root, "does-not-exist"), _factory).ShouldBeEmpty();

    [Fact]
    public void Probe_MultipleWorkspaces_ClassifiesEachIndependently()
    {
        MakeWorkspace("s1", Complete("s1", 0));
        MakeWorkspace("f1", Complete("f1", 2));
        MakeWorkspace("r1", Running("r1"));

        var byStatus = WorkspaceProbe.Probe(_root, _factory)
            .ToLookup(c => c.Status);

        byStatus[WorkspaceStatus.Succeeded].Count().ShouldBe(1);
        byStatus[WorkspaceStatus.Failed].Count().ShouldBe(1);
        byStatus[WorkspaceStatus.Active].Count().ShouldBe(1);
    }
}
