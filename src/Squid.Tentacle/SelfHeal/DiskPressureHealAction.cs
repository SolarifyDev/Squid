using Serilog;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.SelfHeal;

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
    private readonly RetentionQuota _quota;

    public DiskPressureHealAction(
        Func<string> workspaceRootProvider,
        Func<string, IReadOnlyList<WorkspaceCandidate>> candidateProbe,
        IWorkspaceCleanupPolicy policy,
        Action<string> removeWorkspace,
        RetentionQuota quota = null,
        TimeSpan? checkInterval = null,
        Func<string, DiskPressure> diskProbe = null)
    {
        _workspaceRootProvider = workspaceRootProvider ?? throw new ArgumentNullException(nameof(workspaceRootProvider));
        _candidateProbe = candidateProbe ?? throw new ArgumentNullException(nameof(candidateProbe));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _removeWorkspace = removeWorkspace ?? throw new ArgumentNullException(nameof(removeWorkspace));
        _diskProbe = diskProbe ?? DefaultDiskProbe;
        _quota = quota ?? RetentionQuota.Default;
        CheckInterval = checkInterval ?? TimeSpan.FromSeconds(30);
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
