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
/// </summary>
public static class ProbeTimeoutPolicy
{
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    public static TimeSpan For(MachineHealthCheckSetting setting, CommunicationStyle style, MachineHealthCheckPolicyDto policy, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return Max(setting.ProbeTimeout, TransportFloor(style, policy, limits));
    }

    /// <summary>The longest probe any transport can be given; sizes the cross-replica lock so it outlives every probe.</summary>
    public static TimeSpan Longest(MachineHealthCheckSetting setting, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return Max(Max(setting.ProbeTimeout, PollingFloor(limits)), Max(ListeningFloor(limits), SshScriptFloor()));
    }

    /// <summary>How long a manual check waits for a probe already running on the same machine: the longest Halibut-based probe, not an SSH script.</summary>
    public static TimeSpan ManualWait(MachineHealthCheckSetting setting, HalibutTimeoutsAndLimits limits)
    {
        if (setting.IsProbeTimeoutConfigured) return setting.ProbeTimeout;

        return Max(setting.ProbeTimeout, Max(PollingFloor(limits), ListeningFloor(limits)));
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

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
