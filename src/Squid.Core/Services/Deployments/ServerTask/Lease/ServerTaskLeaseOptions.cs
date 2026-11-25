using Squid.Core.Settings.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask.Lease;

/// <summary>
/// How a running task proves its process is alive and how long silence is trusted before the reaper recovers it: the validated,
/// effective form of <see cref="ServerTaskLeaseSettings"/> (configuration section ServerTask:Lease).
/// </summary>
public sealed class ServerTaskLeaseOptions
{
    public const int DefaultRenewIntervalSeconds = 20;
    public const int MinRenewIntervalSeconds = 5;
    public const int DefaultStaleSeconds = 300;
    public const int DefaultMaxAutoResumes = 3;
    public const int ReaperBatchSize = 50;

    /// <summary>A lease goes stale only after this many renewal intervals pass without one.</summary>
    public const int MinStaleIntervals = 3;

    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(DefaultRenewIntervalSeconds);

    public TimeSpan Stale { get; init; } = TimeSpan.FromSeconds(DefaultStaleSeconds);

    /// <summary>How many times the reaper resumes a recovered task by itself before leaving it Paused for an operator; 0 never resumes automatically.</summary>
    public int MaxAutoResumes { get; init; } = DefaultMaxAutoResumes;

    /// <summary>
    /// How long a run may go without one successful renewal before it assumes the lease is lost and stops: two renewal intervals short of the stale
    /// threshold, so it stops BEFORE the reaper may recover the task (the reaper's clock is the database's; this one is local and only counts from the
    /// last answer received, so the margin absorbs a slow answer and the tick granularity). Always positive: Stale is at least three intervals.
    /// </summary>
    public TimeSpan SelfFence => Stale - 2 * RenewInterval;

    public static ServerTaskLeaseOptions From(ServerTaskSetting setting)
    {
        var lease = setting.Lease ?? new ServerTaskLeaseSettings();
        var interval = Math.Max(MinRenewIntervalSeconds, Positive(lease.RenewIntervalSeconds, DefaultRenewIntervalSeconds));
        var stale = Math.Max(interval * MinStaleIntervals, Positive(lease.StaleSeconds, DefaultStaleSeconds));

        return new ServerTaskLeaseOptions { RenewInterval = TimeSpan.FromSeconds(interval), Stale = TimeSpan.FromSeconds(stale), MaxAutoResumes = NotNegative(lease.MaxAutoResumes, DefaultMaxAutoResumes) };
    }

    // Zero is a valid value where it means "off" (MaxAutoResumes); only a negative number is nonsense.
    private static int NotNegative(int value, int fallback) => value >= 0 ? value : fallback;

    private static int Positive(int value, int fallback) => value > 0 ? value : fallback;
}
