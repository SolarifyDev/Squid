namespace Squid.Core.Services.Machines.HealthCheck;

public sealed record MachineHealthCheckOptions(TimeSpan ProbeTimeout, int MaxParallelism, TimeSpan SweepDeadline)
{
    public const string ProbeTimeoutSecondsEnvVar = "SQUID_HEALTH_CHECK_PROBE_TIMEOUT_SECONDS";
    public const string MaxParallelismEnvVar = "SQUID_HEALTH_CHECK_MAX_PARALLELISM";
    public const string SweepDeadlineSecondsEnvVar = "SQUID_HEALTH_CHECK_SWEEP_DEADLINE_SECONDS";

    public const int DefaultProbeTimeoutSeconds = 75;
    public const int DefaultMaxParallelism = 6;
    public const int DefaultSweepDeadlineSeconds = 300;

    public static MachineHealthCheckOptions FromEnvironment() => new(
        TimeSpan.FromSeconds(ReadPositiveInt(ProbeTimeoutSecondsEnvVar, DefaultProbeTimeoutSeconds)),
        ReadPositiveInt(MaxParallelismEnvVar, DefaultMaxParallelism),
        TimeSpan.FromSeconds(ReadPositiveInt(SweepDeadlineSecondsEnvVar, DefaultSweepDeadlineSeconds)));

    private static int ReadPositiveInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);

        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
