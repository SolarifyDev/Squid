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

    // Backoff state: when a sweep reclaims everything it is allowed to but the
    // disk is STILL under pressure (non-workspace usage, or everything protected
    // by the retention window), re-running every CheckInterval just churns. We
    // back off exponentially up to _maxInterval and warn once per episode, then
    // reset the moment pressure clears.
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxInterval;
    private TimeSpan _currentInterval;
    private bool _underPressureWarned;

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

        _baseInterval = checkInterval ?? TimeSpan.FromSeconds(30);
        _maxInterval = TimeSpan.FromTicks(_baseInterval.Ticks * 16);
        _currentInterval = _baseInterval;
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

    public TimeSpan CheckInterval => _currentInterval;

    private static DiskPressure DefaultDiskProbe(string path)
    {
        var (available, total) = DiskSpaceChecker.GetDiskSpace(path);
        return new DiskPressure(available, total,
            SelfHealOptions.Default.LowFreePercentage, SelfHealOptions.Default.CriticalFreePercentage);
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
        {
            ResetBackoff();
            return Task.FromResult(SelfHealOutcome.Healthy(Name));
        }

        // Veto candidates that a script backend still reports as live. This is the
        // race-safe cleanup guarantee: even if the workspace's Output.log looks
        // stale, we never delete a ticket that is still being tracked in memory.
        var candidates = _candidateProbe(workspaceRoot)
            .Where(c => !IsLiveScript(c.Path))
            .ToList();

        var toRemove = _policy.SelectForRemoval(candidates, pressure, _quota);

        var (freed, removed) = RemoveWorkspaces(toRemove, ct);

        // Re-measure only if we actually freed something; otherwise the pressure is unchanged.
        var post = removed > 0 ? _diskProbe(workspaceRoot) : pressure;

        if (!post.IsLow)
        {
            ResetBackoff();
            return Task.FromResult(SelfHealOutcome.RepairPerformed(Name, Summary(pressure, removed, freed)));
        }

        // Reclaimed everything we were allowed to, but the disk is STILL under
        // pressure — remaining usage is protected by the retention window or is not
        // workspace-driven. Warn once per episode + back off so we don't churn the
        // disk every tick (and re-delete a freshly-completed workspace the instant
        // it ages past the keep-set).
        WarnUnderPressureOnce(post, removed, freed);
        BackOff();

        return Task.FromResult(removed > 0
            ? SelfHealOutcome.RepairPerformed(Name, $"{Summary(pressure, removed, freed)}; still {post.FreePercentage:P1} free — backing off to {_currentInterval}")
            : SelfHealOutcome.Healthy(Name));
    }

    private (long Freed, int Removed) RemoveWorkspaces(IReadOnlyList<WorkspaceCandidate> toRemove, CancellationToken ct)
    {
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

        return (freed, removed);
    }

    private static string Summary(DiskPressure pressure, int removed, long freed)
        => $"disk pressure {pressure.FreePercentage:P1} free — removed {removed} workspace(s), reclaimed {DiskSpaceChecker.FormatBytes(freed)}";

    private void WarnUnderPressureOnce(DiskPressure post, int removed, long freed)
    {
        if (_underPressureWarned) return;

        _underPressureWarned = true;
        Log.Warning("[SelfHeal] Disk still under pressure ({Free:P1} free) after reclaiming {Removed} workspace(s) ({Freed}) — " +
            "remaining usage is protected by the retention window or is not workspace-driven. Backing off the heal sweep.",
            post.FreePercentage, removed, DiskSpaceChecker.FormatBytes(freed));
    }

    private void BackOff()
    {
        var doubled = TimeSpan.FromTicks(Math.Min(_currentInterval.Ticks * 2, _maxInterval.Ticks));
        _currentInterval = doubled < _baseInterval ? _baseInterval : doubled;
    }

    private void ResetBackoff()
    {
        _currentInterval = _baseInterval;
        _underPressureWarned = false;
    }
}
