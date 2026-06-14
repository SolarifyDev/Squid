namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// Thrown by <see cref="MachineCircuitBreaker"/> when the breaker is Open —
/// indicates an attempt to reach a machine that has recently failed enough
/// times to warrant fail-fast. This is NOT a transient pause: the breaker only
/// opens after the failure threshold (a SUSTAINED agent problem, not a one-off
/// blip) and is raised BEFORE any script is dispatched, so there is no in-flight
/// script to re-attach to. <see cref="TransientFailureClassifier"/> deliberately
/// EXCLUDES it, so it fails the deployment for an operator to investigate rather
/// than pause-looping on a dead agent.
/// </summary>
public sealed class CircuitOpenException : Exception
{
    public int MachineId { get; }
    public DateTimeOffset ReopensAt { get; }

    public CircuitOpenException(int machineId, DateTimeOffset reopensAt)
        : base($"Circuit breaker is open for machine {machineId}; retry after {reopensAt:O}")
    {
        MachineId = machineId;
        ReopensAt = reopensAt;
    }
}
