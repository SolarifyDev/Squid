using Halibut.Diagnostics;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.HealthCheck;

/// <summary>
/// The probe timeout for one machine. An operator-configured <c>MachineHealthCheck:ProbeTimeoutSeconds</c> is always honoured exactly.
/// Otherwise the default is the larger of the base default and what the transport itself can legitimately take, so a slow-but-alive machine is not
/// declared Unavailable before the transport's own timeout: a polling agent's request may wait the polling-queue timeout for the agent to
/// connect, a listening Tentacle goes through Halibut's connect retries, an SSH probe makes two connects with the machine policy's connect timeout
/// plus its echo and SFTP retries (and a custom health script its own 5 minute limit), and an OpenClaw gateway request may take the policy's connect timeout.
/// One rule ties every default to the sweep: a default probe's full timeout plus <see cref="RecordGrace"/> (the time the coordinator needs to record its result)
/// takes at most <see cref="SweepFraction"/> of the sweep deadline, so it fits a fresh sweep with the rest left for listing the due machines. The coordinator
/// uses the same <see cref="SweepNeed"/> to decide whether a probe still fits the sweep it is in. A probe still running when the sweep ends records nothing.
/// </summary>
public static class ProbeTimeoutPolicy
{
    // Covers Halibut surfacing its own timeout error plus the coordinator's 1s grace, so the transport's error text wins over our generic timeout.
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);

    internal const double SweepFraction = 0.9;

    /// <summary>Time a sweep must still have after a probe's full timeout: the coordinator's 1s wait for the probe's own error text plus recording the result.</summary>
    internal static readonly TimeSpan RecordGrace = TimeSpan.FromSeconds(5);

    // Only reached by a sweep deadline under ~7s, where no default can fit at all; such probes take the coordinator's cannot-fit path.
    private static readonly TimeSpan MinimumDefault = TimeSpan.FromSeconds(1);

    /// <summary>The sweep time a probe with <paramref name="timeout"/> needs to finish on its own timeout and have its result recorded.</summary>
    internal static TimeSpan SweepNeed(TimeSpan timeout) => timeout + RecordGrace;

    public static TimeSpan For(MachineHealthCheckSetting setting, CommunicationStyle style, MachineHealthCheckPolicyDto policy, HalibutTimeoutsAndLimits limits, MachineConnectivityPolicyDto connectivity = null)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return CapToSweep(setting, Max(setting.ProbeTimeout, TransportFloor(style, policy, connectivity, limits)));
    }

    /// <summary>The longest probe any transport can be given; sizes the cross-replica lock so it outlives every probe.</summary>
    public static TimeSpan Longest(MachineHealthCheckSetting setting, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return CapToSweep(setting, Max(Max(setting.ProbeTimeout, PollingFloor(limits)), Max(ListeningFloor(limits), SshFloor(null, customScript: true))));
    }

    /// <summary>How long a manual check waits for a probe already running on the same machine: the longest Halibut-based probe, not an SSH script.</summary>
    public static TimeSpan ManualWait(MachineHealthCheckSetting setting, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return CapToSweep(setting, Max(setting.ProbeTimeout, Max(PollingFloor(limits), ListeningFloor(limits))));
    }

    private static TimeSpan TransportFloor(CommunicationStyle style, MachineHealthCheckPolicyDto policy, MachineConnectivityPolicyDto connectivity, HalibutTimeoutsAndLimits limits) => style switch
    {
        CommunicationStyle.TentaclePolling or CommunicationStyle.KubernetesAgent => PollingFloor(limits),
        CommunicationStyle.TentacleListening => ListeningFloor(limits),
        CommunicationStyle.Ssh => SshFloor(connectivity, SshHealthCheckStrategy.ResolveCustomScript(policy) != null),
        CommunicationStyle.OpenClaw => OpenClawHealthCheckStrategy.ConnectTimeoutFor(connectivity) + Margin,
        _ => TimeSpan.Zero
    };

    private static TimeSpan PollingFloor(HalibutTimeoutsAndLimits limits) => limits.PollingRequestQueueTimeout + Margin;

    private static TimeSpan ListeningFloor(HalibutTimeoutsAndLimits limits) => limits.TcpClientConnectTimeout + TimeSpan.FromTicks(limits.RetryListeningSleepInterval.Ticks * limits.RetryCountLimit) + Margin;

    // The steps the SSH probe itself bounds: the SSH connect, the echo, the SFTP connect, the SFTP listing's retry backoff, and a custom script's own limit.
    private static TimeSpan SshFloor(MachineConnectivityPolicyDto connectivity, bool customScript)
    {
        var connect = SshHealthCheckStrategy.ConnectTimeoutFor(connectivity);
        var script = customScript ? SshHealthCheckStrategy.ScriptTimeout : TimeSpan.Zero;

        return connect + SshHealthCheckStrategy.EchoTimeout + connect + SshRetryHelper.TotalBackoff(SshRetryHelper.DefaultMaxAttempts) + script + Margin;
    }

    private static TimeSpan CapToSweep(MachineHealthCheckSetting setting, TimeSpan timeout)
    {
        var cap = Max(TimeSpan.FromTicks((long)(setting.SweepDeadline.Ticks * SweepFraction)) - RecordGrace, MinimumDefault);

        return timeout < cap ? timeout : cap;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
