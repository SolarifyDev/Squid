namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// How a confirmation survives the database: how often a transient failure is retried inside the job, how often the running import
/// proves it is alive, and how long a silent one is trusted before the recovery sweep returns its session to Validated. Operators tune
/// them with the env vars below; the names are pinned by tests because renaming one silently drops an operator's override.
/// </summary>
public sealed class OctopusImportConfirmationResilienceOptions
{
    public const string TransientRetriesEnvVar = "SQUID_OCTOPUS_IMPORT_CONFIRM_TRANSIENT_RETRIES";
    public const string HeartbeatIntervalSecondsEnvVar = "SQUID_OCTOPUS_IMPORT_CONFIRM_HEARTBEAT_INTERVAL_SECONDS";
    public const string HeartbeatStaleSecondsEnvVar = "SQUID_OCTOPUS_IMPORT_CONFIRM_HEARTBEAT_STALE_SECONDS";

    public const int DefaultTransientRetries = 2;
    public const int MaxTransientRetries = 5;
    public const int DefaultHeartbeatIntervalSeconds = 30;
    public const int MinHeartbeatIntervalSeconds = 5;
    public const int DefaultHeartbeatStaleSeconds = 300;

    /// <summary>A heartbeat goes stale only after this many intervals pass without one.</summary>
    public const int MinStaleIntervals = 3;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);

    public int TransientRetries { get; init; } = DefaultTransientRetries;

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(DefaultHeartbeatIntervalSeconds);

    public TimeSpan HeartbeatStale { get; init; } = TimeSpan.FromSeconds(DefaultHeartbeatStaleSeconds);

    public int MaxAttempts => TransientRetries + 1;

    public static OctopusImportConfirmationResilienceOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    public static OctopusImportConfirmationResilienceOptions From(Func<string, string> read)
    {
        var interval = Math.Max(MinHeartbeatIntervalSeconds, ReadInt(read, HeartbeatIntervalSecondsEnvVar, DefaultHeartbeatIntervalSeconds));
        var stale = Math.Max(interval * MinStaleIntervals, ReadInt(read, HeartbeatStaleSecondsEnvVar, DefaultHeartbeatStaleSeconds));

        return new OctopusImportConfirmationResilienceOptions { TransientRetries = ReadRetries(read), HeartbeatInterval = TimeSpan.FromSeconds(interval), HeartbeatStale = TimeSpan.FromSeconds(stale) };
    }

    /// <summary>2s before the first retry, 8s before the second (×4 each time), spread by ±<paramref name="jitter"/> so retries do not collide.</summary>
    public static TimeSpan RetryDelay(int retry, double jitter) => FirstRetryDelay * Math.Pow(4, Math.Max(0, retry - 1)) * (1 + jitter);

    private static int ReadRetries(Func<string, string> read)
    {
        var retries = ReadInt(read, TransientRetriesEnvVar, DefaultTransientRetries);

        return retries < 0 ? DefaultTransientRetries : Math.Min(retries, MaxTransientRetries);
    }

    private static int ReadInt(Func<string, string> read, string name, int fallback) => int.TryParse(read(name), out var value) ? value : fallback;
}
