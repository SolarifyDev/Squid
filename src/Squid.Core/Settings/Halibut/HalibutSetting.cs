namespace Squid.Core.Settings.Halibut;

public class HalibutSetting : IConfigurationSetting
{
    public HalibutSetting() { }

    public HalibutSetting(IConfiguration configuration)
    {
        var section = configuration.GetSection("Halibut");

        Polling = section.GetSection("Polling").Get<PollingSettings>() ?? new PollingSettings();
        Observer = section.GetSection("Observer").Get<ObserverSettings>() ?? new ObserverSettings();
        Liveness = section.GetSection("Liveness").Get<LivenessSettings>() ?? new LivenessSettings();
        CircuitBreaker = section.GetSection("CircuitBreaker").Get<CircuitBreakerSettings>() ?? new CircuitBreakerSettings();
        RpcRetry = section.GetSection("RpcRetry").Get<RpcRetrySettings>() ?? new RpcRetrySettings();
        Trust = section.GetSection("Trust").Get<TrustSettings>() ?? new TrustSettings();
        DataStreamStorage = section.GetSection("DataStreamStorage").Get<DataStreamStorageSettings>() ?? new DataStreamStorageSettings();
    }

    public PollingSettings Polling { get; set; } = new();
    public ObserverSettings Observer { get; set; } = new();
    public LivenessSettings Liveness { get; set; } = new();
    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();
    public RpcRetrySettings RpcRetry { get; set; } = new();
    public TrustSettings Trust { get; set; } = new();
    public DataStreamStorageSettings DataStreamStorage { get; set; } = new();
}

/// <summary>
/// How a server replica keeps its polling-agent trust list converged with the database, so an agent registered (or removed) through one
/// replica is accepted (or rejected) by the others without a restart. A value of zero or less is invalid and falls back to its default.
/// </summary>
public class TrustSettings
{
    public const int DefaultRefreshIntervalSeconds = 15;
    public const int DefaultUnauthorizedRefreshMinIntervalSeconds = 2;

    /// <summary>How often the trust list is re-read from the database.</summary>
    public int RefreshIntervalSeconds { get; set; } = DefaultRefreshIntervalSeconds;

    /// <summary>Minimum gap between refreshes triggered by connections from unknown clients, so unauthenticated traffic cannot drive database load.</summary>
    public int UnauthorizedRefreshMinIntervalSeconds { get; set; } = DefaultUnauthorizedRefreshMinIntervalSeconds;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(RefreshIntervalSeconds > 0 ? RefreshIntervalSeconds : DefaultRefreshIntervalSeconds);

    public TimeSpan UnauthorizedRefreshMinInterval => TimeSpan.FromSeconds(UnauthorizedRefreshMinIntervalSeconds > 0 ? UnauthorizedRefreshMinIntervalSeconds : DefaultUnauthorizedRefreshMinIntervalSeconds);
}

/// <summary>
/// Where the data streams of a queued Halibut request wait for the pod that holds the agent's connection (the shared database).
/// A value of zero or less is invalid and falls back to its default; a chunk size above <see cref="MaxChunkBytes"/> is lowered to it.
/// </summary>
public class DataStreamStorageSettings
{
    public const int DefaultChunkBytes = 1024 * 1024;
    public const int MaxChunkBytes = 32 * 1024 * 1024;
    public const int DefaultOrphanTtlMinutes = 60;

    /// <summary>Size of one stored chunk. Each chunk is one row and the only buffer a store or a read holds, so memory stays near this size however large the stream is.</summary>
    public int ChunkBytes { get; set; } = DefaultChunkBytes;

    /// <summary>Age after which a stored stream nobody collected (its request was cancelled, timed out or never picked up) is removed. Must outlast the longest request pickup wait.</summary>
    public int OrphanTtlMinutes { get; set; } = DefaultOrphanTtlMinutes;
}

/// <summary>
/// Time budgets for retrying a script RPC (GetStatus / Cancel / Complete) that failed with a transient
/// transport error. A value of zero or less is invalid and falls back to its default.
/// </summary>
public class RpcRetrySettings
{
    public const int DefaultBudgetSeconds = 120;
    public const int DefaultCancelBudgetSeconds = 20;
    public const int DefaultCompleteBudgetSeconds = 30;

    /// <summary>Total budget for GetStatus retries (also capped by the script's remaining timeout).</summary>
    public int BudgetSeconds { get; set; } = DefaultBudgetSeconds;

    /// <summary>Shorter budget for CancelScript so a cancel to a dead agent stays bounded.</summary>
    public int CancelBudgetSeconds { get; set; } = DefaultCancelBudgetSeconds;

    /// <summary>Best-effort budget for CompleteScript.</summary>
    public int CompleteBudgetSeconds { get; set; } = DefaultCompleteBudgetSeconds;
}

public class PollingSettings
{
    public int Port { get; set; } = 10943;
    public bool Enabled { get; set; }
    public int ScriptTimeoutMinutes { get; set; } = 30;
}

public class ObserverSettings
{
    /// <summary>Initial poll interval for GetStatus.</summary>
    public int InitialPollIntervalMs { get; set; } = 1000;

    /// <summary>Upper bound for poll interval after backoff.</summary>
    public int MaxPollIntervalMs { get; set; } = 10_000;

    /// <summary>Multiplier applied per poll for backoff.</summary>
    public double PollBackoffFactor { get; set; } = 1.5;

    /// <summary>Hard cap on in-memory log buffer (script-level) before truncation.</summary>
    public int MaxLogEntries { get; set; } = 100_000;
}

public class LivenessSettings
{
    /// <summary>How often to probe agent liveness during script execution.</summary>
    public int ProbeIntervalSeconds { get; set; } = 5;

    /// <summary>Per-probe timeout. Should be well under ProbeIntervalSeconds.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 3;

    /// <summary>Consecutive failed probes that mark an agent unreachable.</summary>
    public int FailureThreshold { get; set; } = 2;
}

public class CircuitBreakerSettings
{
    /// <summary>Consecutive transport failures before the breaker opens for a machine.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Seconds the breaker stays open (fail-fast) before allowing a probe.</summary>
    public int OpenDurationSeconds { get; set; } = 60;
}
