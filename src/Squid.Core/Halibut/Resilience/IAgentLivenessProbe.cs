using Halibut;

namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// Fast liveness check for a Halibut-connected agent. The probe is used by
/// the script observer as a second signal alongside the main polling loop:
/// if the agent fails a configured number of consecutive probes, the
/// observer stops waiting and fails the current script as a transient
/// error instead of absorbing the full ScriptTimeoutMinutes window.
/// </summary>
public interface IAgentLivenessProbe
{
    Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct);
}
