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
    public void Probe_MeasuresWorkspaceSize()
    {
        var payload = new string('x', 4096);
        MakeWorkspace("sized", Complete("sized", 0), content: payload);

        var c = WorkspaceProbe.Probe(_root, _factory).ShouldHaveSingleItem();

        c.SizeBytes.ShouldBeGreaterThanOrEqualTo(4096,
            customMessage: "Size must include the workspace's files so the policy can reclaim enough space.");
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
