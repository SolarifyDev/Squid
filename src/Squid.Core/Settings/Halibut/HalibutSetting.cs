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
    }

    public PollingSettings Polling { get; set; } = new();
    public ObserverSettings Observer { get; set; } = new();
    public LivenessSettings Liveness { get; set; } = new();
    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();
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
