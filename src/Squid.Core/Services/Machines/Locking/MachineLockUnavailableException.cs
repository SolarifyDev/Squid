namespace Squid.Core.Services.Machines.Locking;

/// <summary>
/// Thrown when a deployment cannot acquire the per-machine dispatch lock before running a
/// script on that machine — either because an upgrade (or another deployment) currently holds
/// it (contention), or because the distributed-lock backend (Redis) is unreachable.
///
/// <para>The deployment runner treats this as a resumable PAUSE, not a failure: an upgrade can
/// restart the agent, so a deployment must never run a script on a machine while an upgrade
/// holds the lock — and it must never proceed without the lock when Redis is down. The
/// deployment is paused with its checkpoint preserved and re-attempts the lock on resume.</para>
/// </summary>
public sealed class MachineLockUnavailableException : InvalidOperationException
{
    public int MachineId { get; }

    /// <summary>True = Redis unreachable (infra); false = contention (another active dispatch holds the lock).</summary>
    public bool IsInfrastructureFailure { get; }

    public MachineLockUnavailableException(int machineId, bool isInfrastructureFailure, string detail, Exception innerException = null)
        : base($"Could not acquire the per-machine dispatch lock for machine {machineId}: {detail}", innerException)
    {
        MachineId = machineId;
        IsInfrastructureFailure = isInfrastructureFailure;
    }
}
