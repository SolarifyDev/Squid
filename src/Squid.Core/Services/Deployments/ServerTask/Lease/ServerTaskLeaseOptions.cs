namespace Squid.Core.Services.Deployments.ServerTask.Lease;

/// <summary>
/// How a running task proves its process is alive and how long silence is trusted before the reaper recovers it. Operators tune them
/// with the env vars below; the names are pinned by tests because renaming one silently drops an operator's override.
/// </summary>
public sealed class ServerTaskLeaseOptions
{
    public const string RenewIntervalSecondsEnvVar = "SQUID_SERVER_TASK_LEASE_RENEW_INTERVAL_SECONDS";
    public const string StaleSecondsEnvVar = "SQUID_SERVER_TASK_LEASE_STALE_SECONDS";
    public const string ReaperEnabledEnvVar = "SQUID_SERVER_TASK_REAPER_ENABLED";

    public const int DefaultRenewIntervalSeconds = 20;
    public const int MinRenewIntervalSeconds = 5;
    public const int DefaultStaleSeconds = 300;
    public const bool DefaultReaperEnabled = true;
    public const int ReaperBatchSize = 50;

    /// <summary>A lease goes stale only after this many renewal intervals pass without one.</summary>
    public const int MinStaleIntervals = 3;

    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(DefaultRenewIntervalSeconds);

    public TimeSpan Stale { get; init; } = TimeSpan.FromSeconds(DefaultStaleSeconds);

    public bool ReaperEnabled { get; init; } = DefaultReaperEnabled;

    public static ServerTaskLeaseOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    public static ServerTaskLeaseOptions From(Func<string, string> read)
    {
        var interval = Math.Max(MinRenewIntervalSeconds, ReadInt(read, RenewIntervalSecondsEnvVar, DefaultRenewIntervalSeconds));
        var stale = Math.Max(interval * MinStaleIntervals, ReadInt(read, StaleSecondsEnvVar, DefaultStaleSeconds));

        return new ServerTaskLeaseOptions { RenewInterval = TimeSpan.FromSeconds(interval), Stale = TimeSpan.FromSeconds(stale), ReaperEnabled = ReadEnabled(read(ReaperEnabledEnvVar)) };
    }

    // Only an explicit falsey token switches the reaper off; anything unrecognised keeps the safe default (on) so a typo cannot leave dead tasks holding slots.
    private static bool ReadEnabled(string raw)
    {
        var value = raw?.Trim();

        if (string.IsNullOrEmpty(value)) return DefaultReaperEnabled;

        var off = value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase);

        return !off;
    }

    private static int ReadInt(Func<string, string> read, string name, int fallback) => int.TryParse(read(name), out var value) && value > 0 ? value : fallback;
}
