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

public sealed record DiskPressure(
    long FreeBytes,
    long TotalBytes,
    double LowFreePercentage = SelfHealOptions.DefaultLowFreePercentage,
    double CriticalFreePercentage = SelfHealOptions.DefaultCriticalFreePercentage)
{
    public double FreePercentage => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 0.0;
    public bool IsLow => FreePercentage < LowFreePercentage;
    public bool IsCritical => FreePercentage < CriticalFreePercentage;
}

public sealed record RetentionQuota(int KeepLatestSucceeded, int KeepLatestFailed)
{
    public static RetentionQuota Default => new(SelfHealOptions.DefaultKeepLatestSucceeded, SelfHealOptions.DefaultKeepLatestFailed);
}

/// <summary>
/// Default cleanup policy:
///   - Active workspaces are never removed.
///   - Not under disk pressure (IsLow == false): nothing to do.
///   - Under pressure: keep the most recent K succeeded + M failed; evict the
///     rest in oldest-first order until free space climbs back above the
///     low-pressure target (the normal target equals the low threshold, raised
///     to <c>criticalTargetFreePercentage</c> under critical pressure).
///
/// <para>Two age floors keep the sweep safe and operator-friendly:</para>
/// <list type="bullet">
///   <item><b>Fresh-grace</b> (<paramref name="freshGraceWindow"/>) protects a
///         workspace whose directory was written within the window — even under
///         critical pressure — so a deployment initialising a brand-new
///         workspace is never deleted out from under it (the TOCTOU gap before
///         the running-script reporter knows about the ticket).</item>
///   <item><b>Retention TTL</b> (<paramref name="minRetentionAge"/>, the
///         operator's orphan-workspace TTL) protects recent completed
///         workspaces so the post-mortem window an operator pinned is honoured —
///         <i>except</i> under critical pressure, where reclaiming disk wins.</item>
/// </list>
/// Both default to zero so the parameterless policy keeps its original
/// no-floor behaviour; the live wiring (<c>SelfHealBackgroundTask.ForLocalWorkspaces</c>)
/// injects the real floors.
/// </summary>
public sealed class DefaultWorkspaceCleanupPolicy : IWorkspaceCleanupPolicy
{
    private readonly double _targetFreePercentage;
    private readonly double _criticalTargetFreePercentage;
    private readonly TimeSpan _minRetentionAge;
    private readonly TimeSpan _freshGraceWindow;
    private readonly Func<DateTimeOffset> _clock;

    public DefaultWorkspaceCleanupPolicy(
        double targetFreePercentage = SelfHealOptions.DefaultLowFreePercentage,
        double criticalTargetFreePercentage = SelfHealOptions.DefaultCriticalTargetFreePercentage,
        TimeSpan minRetentionAge = default,
        TimeSpan freshGraceWindow = default,
        Func<DateTimeOffset> clock = null)
    {
        _targetFreePercentage = targetFreePercentage;
        _criticalTargetFreePercentage = criticalTargetFreePercentage;
        _minRetentionAge = minRetentionAge;
        _freshGraceWindow = freshGraceWindow;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

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

        var floor = EffectiveAgeFloor(pressure);
        var now = _clock();

        var removable = candidates
            .Where(c => c.Status != WorkspaceStatus.Active && !keep.Contains(c.Path))
            .Where(c => now - c.LastModifiedUtc >= floor)       // honour fresh-grace + retention TTL
            .OrderBy(c => c.LastModifiedUtc)                    // oldest first
            .ToList();

        if (removable.Count == 0) return Array.Empty<WorkspaceCandidate>();

        var targetFreePct = pressure.IsCritical ? _criticalTargetFreePercentage : _targetFreePercentage;
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

    // Under critical pressure only the short fresh-grace floor applies (reclaim
    // disk wins over post-mortem retention); otherwise the longer of the two.
    private TimeSpan EffectiveAgeFloor(DiskPressure pressure)
        => pressure.IsCritical
            ? _freshGraceWindow
            : (_freshGraceWindow > _minRetentionAge ? _freshGraceWindow : _minRetentionAge);

    private static IEnumerable<string> LatestByStatus(IReadOnlyList<WorkspaceCandidate> candidates, WorkspaceStatus status, int count)
        => candidates
            .Where(c => c.Status == status)
            .OrderByDescending(c => c.LastModifiedUtc)
            .Take(count)
            .Select(c => c.Path);
}
