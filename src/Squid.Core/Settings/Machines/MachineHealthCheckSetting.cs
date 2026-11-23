namespace Squid.Core.Settings.Machines;

public class MachineHealthCheckSetting : IConfigurationSetting
{
    public const string SectionName = "MachineHealthCheck";

    private double _probeTimeoutSeconds = 75;
    private bool _probeTimeoutConfigured;
    private int _maxParallelism = 6;
    private double _sweepDeadlineSeconds = 300;

    public MachineHealthCheckSetting() { }

    public MachineHealthCheckSetting(IConfiguration configuration)
    {
        configuration.GetSection(SectionName).Bind(this);
    }

    /// <summary>
    /// Per-probe timeout; a probe that exceeds it records the machine Unavailable. When set it applies to every transport exactly as given.
    /// When left unset the base value is 75 s and each transport raises it to what that transport can legitimately take (see <see cref="Services.Machines.HealthCheck.ProbeTimeoutPolicy"/>).
    /// Values &lt;= 0 are ignored.
    /// </summary>
    public double ProbeTimeoutSeconds
    {
        get => _probeTimeoutSeconds;
        set
        {
            if (value <= 0) return;

            _probeTimeoutSeconds = value;
            _probeTimeoutConfigured = true;
        }
    }

    /// <summary>True when an operator set <see cref="ProbeTimeoutSeconds"/>; the per-transport defaults then no longer apply.</summary>
    public bool IsProbeTimeoutConfigured => _probeTimeoutConfigured;

    /// <summary>Machines probed concurrently by one sweep. Values &lt;= 0 keep the default.</summary>
    public int MaxParallelism
    {
        get => _maxParallelism;
        set { if (value > 0) _maxParallelism = value; }
    }

    /// <summary>Upper bound on one sweep; machines not reached stay due for the next sweep. Values &lt;= 0 keep the default.</summary>
    public double SweepDeadlineSeconds
    {
        get => _sweepDeadlineSeconds;
        set { if (value > 0) _sweepDeadlineSeconds = value; }
    }

    public TimeSpan ProbeTimeout => TimeSpan.FromSeconds(ProbeTimeoutSeconds);

    public TimeSpan SweepDeadline => TimeSpan.FromSeconds(SweepDeadlineSeconds);
}
