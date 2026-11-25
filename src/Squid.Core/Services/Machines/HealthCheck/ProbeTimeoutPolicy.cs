using Halibut.Diagnostics;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.HealthCheck;

/// <summary>
/// The probe timeout for one machine. An operator-configured <c>MachineHealthCheck:ProbeTimeoutSeconds</c> is always honoured exactly.
/// Otherwise the default is the larger of the base default and what the transport itself can legitimately take, so a slow-but-alive machine is not
/// declared Unavailable before the transport's own timeout: a polling agent's request may wait the polling-queue timeout for the agent to
/// connect, a listening Tentacle goes through Halibut's connect retries, and an SSH custom health script has its own 5 minute limit.
/// No default ever exceeds <see cref="SweepFraction"/> of the sweep deadline: a probe still running when the sweep is cancelled records nothing, so the machine would
/// stay due, hold a parallel slot for the whole sweep and never be marked Unavailable. The cap is one rule for every transport.
/// </summary>
public static class ProbeTimeoutPolicy
{
    // Covers Halibut surfacing its own timeout error plus the coordinator's 1s grace, so the transport's error text wins over our generic timeout.
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);

    internal const double SweepFraction = 0.9;

    public static TimeSpan For(MachineHealthCheckSetting setting, CommunicationStyle style, MachineHealthCheckPolicyDto policy, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return CapToSweep(setting, Max(setting.ProbeTimeout, TransportFloor(style, policy, limits)));
    }

    /// <summary>The longest probe any transport can be given; sizes the cross-replica lock so it outlives every probe.</summary>
    public static TimeSpan Longest(MachineHealthCheckSetting setting, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return CapToSweep(setting, Max(Max(setting.ProbeTimeout, PollingFloor(limits)), Max(ListeningFloor(limits), SshScriptFloor())));
    }

    /// <summary>How long a manual check waits for a probe already running on the same machine: the longest Halibut-based probe, not an SSH script.</summary>
    public static TimeSpan ManualWait(MachineHealthCheckSetting setting, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return CapToSweep(setting, Max(setting.ProbeTimeout, Max(PollingFloor(limits), ListeningFloor(limits))));
    }

    private static TimeSpan TransportFloor(CommunicationStyle style, MachineHealthCheckPolicyDto policy, HalibutTimeoutsAndLimits limits) => style switch
    {
        CommunicationStyle.TentaclePolling or CommunicationStyle.KubernetesAgent => PollingFloor(limits),
        CommunicationStyle.TentacleListening => ListeningFloor(limits),
        CommunicationStyle.Ssh when SshHealthCheckStrategy.ResolveCustomScript(policy) != null => SshScriptFloor(),
        _ => TimeSpan.Zero
    };

    private static TimeSpan PollingFloor(HalibutTimeoutsAndLimits limits) => limits.PollingRequestQueueTimeout + Margin;

    private static TimeSpan ListeningFloor(HalibutTimeoutsAndLimits limits) => limits.TcpClientConnectTimeout + TimeSpan.FromTicks(limits.RetryListeningSleepInterval.Ticks * limits.RetryCountLimit) + Margin;

    private static TimeSpan SshScriptFloor() => SshHealthCheckStrategy.ScriptTimeout + TimeSpan.FromSeconds(SshHealthCheckStrategy.DefaultConnectTimeoutSeconds) + Margin;

    private static TimeSpan CapToSweep(MachineHealthCheckSetting setting, TimeSpan timeout)
    {
        var cap = TimeSpan.FromTicks((long)(setting.SweepDeadline.Ticks * SweepFraction));

        return timeout < cap ? timeout : cap;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
