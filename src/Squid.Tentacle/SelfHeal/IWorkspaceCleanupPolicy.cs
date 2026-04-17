namespace Squid.Tentacle.SelfHeal;

/// <summary>
/// Decides which completed-script workspaces to remove when the agent is under
/// disk pressure. Octopus uses a simple age-based policy; we layer pressure +
/// LRU + per-status retention on top so a flood of short-lived deployments
/// never starves the disk while still keeping a rolling window of recent
/// failures available for post-mortem.
/// </summary>
public interface IWorkspaceCleanupPolicy
{
    IReadOnlyList<WorkspaceCandidate> SelectForRemoval(
        IReadOnlyList<WorkspaceCandidate> candidates,
        DiskPressure pressure,
        RetentionQuota quota);
}

public sealed record WorkspaceCandidate(
    string Path,
    DateTimeOffset LastModifiedUtc,
    long SizeBytes,
    WorkspaceStatus Status);

public enum WorkspaceStatus
{
    Active,      // script still running — never a candidate
    Succeeded,
    Failed,
    Unknown
}

public sealed record DiskPressure(long FreeBytes, long TotalBytes)
{
    public double FreePercentage => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 0.0;
    public bool IsLow => FreePercentage < 0.20;
    public bool IsCritical => FreePercentage < 0.10;
}

public sealed record RetentionQuota(int KeepLatestSucceeded, int KeepLatestFailed)
{
    public static RetentionQuota Default => new(KeepLatestSucceeded: 10, KeepLatestFailed: 20);
}

/// <summary>
/// Default cleanup policy:
///   - Active workspaces are never removed.
///   - Not under disk pressure (IsLow == false): nothing to do.
///   - Under pressure: keep the most recent K succeeded + M failed; evict the
///     rest in oldest-first order until free space climbs back above the
///     low-pressure threshold (20% free, or 30% under critical pressure).
/// </summary>
public sealed class DefaultWorkspaceCleanupPolicy : IWorkspaceCleanupPolicy
{
    public IReadOnlyList<WorkspaceCandidate> SelectForRemoval(
        IReadOnlyList<WorkspaceCandidate> candidates,
        DiskPressure pressure,
        RetentionQuota quota)
    {
        if (candidates == null) return Array.Empty<WorkspaceCandidate>();
        if (!pressure.IsLow) return Array.Empty<WorkspaceCandidate>();

        var keep = new HashSet<string>();
        keep.UnionWith(LatestByStatus(candidates, WorkspaceStatus.Succeeded, quota.KeepLatestSucceeded));
        keep.UnionWith(LatestByStatus(candidates, WorkspaceStatus.Failed, quota.KeepLatestFailed));

        var removable = candidates
            .Where(c => c.Status != WorkspaceStatus.Active && !keep.Contains(c.Path))
            .OrderBy(c => c.LastModifiedUtc)                    // oldest first
            .ToList();

        if (removable.Count == 0) return Array.Empty<WorkspaceCandidate>();

        var targetFreePct = pressure.IsCritical ? 0.30 : 0.20;
        var requiredBytes = (long)(pressure.TotalBytes * targetFreePct) - pressure.FreeBytes;

        if (requiredBytes <= 0) return Array.Empty<WorkspaceCandidate>();

        var selected = new List<WorkspaceCandidate>();
        long reclaimed = 0;

        foreach (var c in removable)
        {
            selected.Add(c);
            reclaimed += c.SizeBytes;
            if (reclaimed >= requiredBytes) break;
        }

        return selected;
    }

    private static IEnumerable<string> LatestByStatus(IReadOnlyList<WorkspaceCandidate> candidates, WorkspaceStatus status, int count)
        => candidates
            .Where(c => c.Status == status)
            .OrderByDescending(c => c.LastModifiedUtc)
            .Take(count)
            .Select(c => c.Path);
}
