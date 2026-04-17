using Serilog;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.SelfHeal;

/// <summary>
/// Given a workspace directory path, returns a ticket id if the directory
/// name follows the <c>squid-tentacle-{ticketId}</c> convention, otherwise
/// <c>null</c>. Used by the heal action to consult
/// <see cref="IRunningScriptReporter"/>s before deletion.
/// </summary>
public delegate string? TicketExtractor(string workspacePath);


/// <summary>
/// On each tick, inspects workspace usage; if free space is below the
/// low-pressure threshold, asks the cleanup policy which workspaces to delete
/// and removes them. Active (running) workspaces are never touched — the
/// policy filter plus the workspace probe's Active detection keep script
/// execution immune from the heal sweep.
/// </summary>
public sealed class DiskPressureHealAction : ISelfHealAction
{
    private readonly Func<string> _workspaceRootProvider;
    private readonly Func<string, IReadOnlyList<WorkspaceCandidate>> _candidateProbe;
    private readonly IWorkspaceCleanupPolicy _policy;
    private readonly Action<string> _removeWorkspace;
    private readonly Func<string, DiskPressure> _diskProbe;
    private readonly IReadOnlyList<IRunningScriptReporter> _runningScriptReporters;
    private readonly TicketExtractor _ticketExtractor;
    private readonly RetentionQuota _quota;

    public DiskPressureHealAction(
        Func<string> workspaceRootProvider,
        Func<string, IReadOnlyList<WorkspaceCandidate>> candidateProbe,
        IWorkspaceCleanupPolicy policy,
        Action<string> removeWorkspace,
        RetentionQuota quota = null,
        TimeSpan? checkInterval = null,
        Func<string, DiskPressure> diskProbe = null,
        IEnumerable<IRunningScriptReporter> runningScriptReporters = null,
        TicketExtractor ticketExtractor = null)
    {
        _workspaceRootProvider = workspaceRootProvider ?? throw new ArgumentNullException(nameof(workspaceRootProvider));
        _candidateProbe = candidateProbe ?? throw new ArgumentNullException(nameof(candidateProbe));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _removeWorkspace = removeWorkspace ?? throw new ArgumentNullException(nameof(removeWorkspace));
        _diskProbe = diskProbe ?? DefaultDiskProbe;
        _runningScriptReporters = runningScriptReporters?.ToList() ?? new List<IRunningScriptReporter>();
        _ticketExtractor = ticketExtractor ?? DefaultTicketExtractor;
        _quota = quota ?? RetentionQuota.Default;
        CheckInterval = checkInterval ?? TimeSpan.FromSeconds(30);
    }

    private static string? DefaultTicketExtractor(string workspacePath)
    {
        // Matches the convention LocalScriptService and ScriptPodService share:
        //   /{temp|workspaceRoot}/squid-tentacle-{ticketId}
        //   /{workspaceRoot}/{ticketId}
        var name = Path.GetFileName(workspacePath);
        if (string.IsNullOrEmpty(name)) return null;

        const string prefix = "squid-tentacle-";
        if (name.StartsWith(prefix, StringComparison.Ordinal))
            return name[prefix.Length..];

        return name;
    }

    private bool IsLiveScript(string workspacePath)
    {
        if (_runningScriptReporters.Count == 0) return false;

        var ticketId = _ticketExtractor(workspacePath);
        if (string.IsNullOrEmpty(ticketId)) return false;

        foreach (var reporter in _runningScriptReporters)
        {
            if (reporter.IsRunningScript(ticketId)) return true;
        }
        return false;
    }

    public string Name => "disk-pressure-cleanup";

    public TimeSpan CheckInterval { get; }

    private static DiskPressure DefaultDiskProbe(string path)
    {
        var (available, total) = DiskSpaceChecker.GetDiskSpace(path);
        return new DiskPressure(available, total);
    }

    public Task<SelfHealOutcome> RunAsync(CancellationToken ct)
    {
        var workspaceRoot = _workspaceRootProvider();
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Task.FromResult(SelfHealOutcome.Healthy(Name));

        var pressure = _diskProbe(workspaceRoot);
        if (pressure.TotalBytes <= 0)
            return Task.FromResult(SelfHealOutcome.Healthy(Name));

        if (!pressure.IsLow)
            return Task.FromResult(SelfHealOutcome.Healthy(Name));

        var candidates = _candidateProbe(workspaceRoot);

        // Veto candidates that a script backend still reports as live. This is the
        // race-safe cleanup guarantee: even if the workspace's Output.log looks
        // stale, we never delete a ticket that is still being tracked in memory.
        candidates = candidates.Where(c => !IsLiveScript(c.Path)).ToList();

        var toRemove = _policy.SelectForRemoval(candidates, pressure, _quota);

        if (toRemove.Count == 0)
            return Task.FromResult(SelfHealOutcome.Healthy(Name));

        var freed = 0L;
        var removed = 0;

        foreach (var cand in toRemove)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                _removeWorkspace(cand.Path);
                freed += cand.SizeBytes;
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SelfHeal] Failed to remove workspace {Path}", cand.Path);
            }
        }

        return Task.FromResult(SelfHealOutcome.RepairPerformed(Name,
            $"disk pressure {pressure.FreePercentage:P1} free — removed {removed} workspace(s), reclaimed {DiskSpaceChecker.FormatBytes(freed)}"));
    }
}
