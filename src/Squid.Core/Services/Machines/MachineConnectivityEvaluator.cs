using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines;

/// <summary>
/// Single source of truth for the machine-policy <c>MachineConnectivityBehavior</c>
/// during health checks. Pure + deterministic so it can be unit-tested exhaustively.
///
/// <para><see cref="MachineConnectivityBehavior.ExpectedToBeOnline"/> (default, and the
/// value when no policy is assigned) means an unreachable agent is a genuine
/// health-check failure — today's behaviour. <see cref="MachineConnectivityBehavior.MayBeOfflineAndCanBeSkipped"/>
/// means an unreachable agent is expected and reported as <i>tolerated</i> rather than
/// a hard failure (for elastic / transient targets that legitimately scale to zero).</para>
/// </summary>
public static class MachineConnectivityEvaluator
{
    public static bool AllowsOffline(MachineConnectivityPolicyDto connectivityPolicy)
        => connectivityPolicy?.MachineConnectivityBehavior == MachineConnectivityBehavior.MayBeOfflineAndCanBeSkipped;
}
