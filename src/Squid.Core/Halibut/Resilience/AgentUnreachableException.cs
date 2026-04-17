namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// Raised when an agent fails the liveness probe more than the configured
/// number of consecutive times. Treated as a transient failure upstream
/// (eligible for retry / circuit-break) rather than a permanent script
/// failure.
/// </summary>
public sealed class AgentUnreachableException : Exception
{
    public string MachineName { get; }
    public int ConsecutiveFailures { get; }

    public AgentUnreachableException(string machineName, int consecutiveFailures)
        : base($"Agent {machineName} failed liveness probe {consecutiveFailures} times in a row")
    {
        MachineName = machineName;
        ConsecutiveFailures = consecutiveFailures;
    }
}
