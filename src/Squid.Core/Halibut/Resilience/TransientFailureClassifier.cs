using Halibut;

namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// The single definition of "is this a TRANSIENT infrastructure failure?" — a
/// blip that should PAUSE a deployment (resumable, re-attach to the still-running
/// script) rather than fail it terminally. Referenced across layers (the pipeline
/// runner's pause-classification, the per-target <c>TargetCatchClassifier</c>, the
/// per-action catch in <c>ExecuteStepsPhase</c>, and the Halibut execution
/// strategy's re-attach probe), so the definition lives in ONE place.
///
/// <para>Transient = the round-trip to the agent could not be completed, or the
/// agent is fail-fast-rejected by an open breaker:</para>
/// <list type="bullet">
///   <item><see cref="CircuitOpenException"/> — the breaker is open; its own
///         contract says treat as transient and retry after the open window.</item>
///   <item><see cref="AgentUnreachableException"/> — the liveness probe gave up.</item>
///   <item>A base/transport <see cref="HalibutClientException"/> — connection
///         reset, EOF, TLS handshake failure, timeout, etc.</item>
/// </list>
///
/// <para>NOT transient — the request reached the agent and was PERMANENTLY
/// rejected, so retrying forever is pointless (and would pause-loop): the Halibut
/// protocol/invocation subtypes (version mismatch → no matching service/method;
/// the agent's service itself threw → service-invocation). These are real
/// failures and must fail the deployment.</para>
/// </summary>
public static class TransientFailureClassifier
{
    public static bool IsTransient(Exception ex)
    {
        if (ex is AggregateException aggregate)
            return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTransient);

        if (ex is CircuitOpenException or AgentUnreachableException)
            return true;

        // Halibut transport failures are transient EXCEPT the protocol/invocation
        // subtypes (the request reached the agent and was permanently rejected).
        if (ex is HalibutClientException)
            return !IsPermanentHalibutFailure(ex);

        return false;
    }

    private static bool IsPermanentHalibutFailure(Exception ex)
        => ex is global::Halibut.Exceptions.ServiceInvocationHalibutClientException
            or global::Halibut.Exceptions.NoMatchingServiceOrMethodHalibutClientException
            or global::Halibut.Exceptions.MethodNotFoundHalibutClientException
            or global::Halibut.Exceptions.ServiceNotFoundHalibutClientException
            or global::Halibut.Exceptions.AmbiguousMethodMatchHalibutClientException;
}
