namespace Squid.Core.Halibut.Resilience;

public enum CircuitBreakerState
{
    /// <summary>Normal operation — calls pass through.</summary>
    Closed,

    /// <summary>Too many recent failures — calls fail-fast until open duration expires.</summary>
    Open,

    /// <summary>Open duration expired — next call is allowed as a probe. Success closes, failure re-opens.</summary>
    HalfOpen
}
