using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Cleanup;

/// <summary>
/// Single source of truth for "is this machine eligible to be auto-deleted by its
/// machine policy's cleanup setting". Pure + deterministic so it can be unit-tested
/// exhaustively; the orchestrating service and the three-mode enforcement decide
/// what to DO with an eligible machine (log vs delete).
///
/// <para>A machine is eligible only when ALL hold: its policy opts in
/// (<see cref="DeleteMachinesBehavior.DeleteUnavailableMachines"/>), it is currently
/// <see cref="MachineHealthStatus.Unavailable"/>, we know WHEN it became unavailable
/// (<paramref name="unavailableSince"/> is non-null), and that instant is at least
/// the configured grace period in the past. An unknown go-bad time (null) is never
/// eligible — we won't delete a target whose downtime duration we can't prove.</para>
/// </summary>
public static class MachineCleanupEvaluator
{
    public static bool IsEligible(MachineCleanupPolicyDto policy, MachineHealthStatus status, DateTimeOffset? unavailableSince, DateTimeOffset now)
    {
        if (policy == null) return false;

        if (policy.DeleteMachinesBehavior != DeleteMachinesBehavior.DeleteUnavailableMachines) return false;

        if (status != MachineHealthStatus.Unavailable) return false;

        if (unavailableSince == null) return false;

        if (policy.DeleteMachinesAfterSeconds <= 0) return false;

        return unavailableSince.Value <= now.AddSeconds(-policy.DeleteMachinesAfterSeconds);
    }
}
