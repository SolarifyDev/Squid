namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// Thrown by <see cref="MachineCircuitBreaker"/> when the breaker is Open —
/// indicates an attempt to reach a machine that has recently failed enough
/// times to warrant fail-fast. Callers should treat this as transient
/// (not a deployment error) and either abort the step or wait for the
/// open duration to expire.
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
