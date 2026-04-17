using Shouldly;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class DiskPressureHealActionReporterTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-reporter-test-{Guid.NewGuid():N}");

    public DiskPressureHealActionReporterTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    private static DiskPressure HighPressure(string _) => new(FreeBytes: 50, TotalBytes: 1000);

    [Fact]
    public async Task Run_CandidateTicketLive_VetoedByReporter_NotRemoved()
    {
        var liveTicket = Guid.NewGuid().ToString("N");
        var ws1 = Path.Combine(_workspace, $"squid-tentacle-{liveTicket}");
        var ws2 = Path.Combine(_workspace, "squid-tentacle-some-other-dead-ticket");
        Directory.CreateDirectory(ws1);
        Directory.CreateDirectory(ws2);

        var removed = new List<string>();
        var candidates = new List<WorkspaceCandidate>
        {
            new(ws1, DateTimeOffset.UtcNow.AddHours(-100), 100, WorkspaceStatus.Succeeded),
            new(ws2, DateTimeOffset.UtcNow.AddHours(-100), 100, WorkspaceStatus.Succeeded)
        };

        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => candidates,
            policy: new AllCandidatesPolicy(),
            removeWorkspace: p => removed.Add(p),
            diskProbe: HighPressure,
            runningScriptReporters: new[] { (IRunningScriptReporter)new FakeReporter(liveTicket) });

        var outcome = await action.RunAsync(CancellationToken.None);

        removed.ShouldNotContain(ws1, "workspace for a live ticket must not be deleted regardless of age / disk pressure");
        removed.ShouldContain(ws2, "dead workspace still gets reclaimed");
        outcome.Healed.ShouldBeTrue();
    }

    [Fact]
    public async Task Run_NoReporters_BehavesAsBefore_RemovesAll()
    {
        var ws = Path.Combine(_workspace, "squid-tentacle-xyz");
        Directory.CreateDirectory(ws);

        var removed = new List<string>();
        var candidates = new List<WorkspaceCandidate>
        {
            new(ws, DateTimeOffset.UtcNow.AddHours(-100), 100, WorkspaceStatus.Succeeded)
        };

        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => candidates,
            policy: new AllCandidatesPolicy(),
            removeWorkspace: p => removed.Add(p),
            diskProbe: HighPressure);   // no reporters configured

        await action.RunAsync(CancellationToken.None);

        removed.ShouldContain(ws);
    }

    [Fact]
    public async Task Run_EmptyReporter_VetoVoteIsRespected()
    {
        var ticket = Guid.NewGuid().ToString("N");
        var ws = Path.Combine(_workspace, $"squid-tentacle-{ticket}");
        Directory.CreateDirectory(ws);

        var removed = new List<string>();
        var candidates = new List<WorkspaceCandidate>
        {
            new(ws, DateTimeOffset.UtcNow.AddHours(-100), 100, WorkspaceStatus.Succeeded)
        };

        // Two reporters: one sees it as dead, one sees it as live → veto wins.
        var action = new DiskPressureHealAction(
            workspaceRootProvider: () => _workspace,
            candidateProbe: _ => candidates,
            policy: new AllCandidatesPolicy(),
            removeWorkspace: p => removed.Add(p),
            diskProbe: HighPressure,
            runningScriptReporters: new IRunningScriptReporter[]
            {
                new FakeReporter(),                // empty — says nothing is live
                new FakeReporter(ticket)           // flags this ticket as live
            });

        await action.RunAsync(CancellationToken.None);

        removed.ShouldBeEmpty("any reporter reporting live must veto removal");
    }

    private sealed class FakeReporter : IRunningScriptReporter
    {
        private readonly HashSet<string> _live;
        public FakeReporter(params string[] liveTickets) => _live = new HashSet<string>(liveTickets);
        public bool IsRunningScript(string ticketId) => _live.Contains(ticketId);
    }

    private sealed class AllCandidatesPolicy : IWorkspaceCleanupPolicy
    {
        public IReadOnlyList<WorkspaceCandidate> SelectForRemoval(
            IReadOnlyList<WorkspaceCandidate> candidates, DiskPressure _, RetentionQuota __)
            => candidates;
    }
}
