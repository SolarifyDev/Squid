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
/// <para>Transient = a single round-trip to the agent could not be completed, so
/// the script may still be running and a resume should re-attach to it:</para>
/// <list type="bullet">
///   <item><see cref="AgentUnreachableException"/> — the liveness probe gave up
///         mid-script (the script was dispatched and may still be running).</item>
///   <item>A base/transport <see cref="HalibutClientException"/> — connection
///         reset, EOF, TLS handshake failure, timeout, etc.</item>
/// </list>
///
/// <para>NOT transient — these are sustained or permanent conditions, so pausing
/// for resume would only pause-loop; they must fail the deployment so an operator
/// investigates:</para>
/// <list type="bullet">
///   <item>The Halibut protocol/invocation subtypes — the request reached the
///         agent and was PERMANENTLY rejected (version mismatch → no matching
///         service/method; the agent's service itself threw).</item>
///   <item><see cref="CircuitOpenException"/> — the per-machine breaker only opens
///         after the failure threshold (3+ consecutive failures), so it signals a
///         SUSTAINED agent problem, not a one-off blip. It is also raised BEFORE
///         any script is dispatched (fail-fast), so there is no in-flight script to
///         re-attach to. Pausing on it would loop on a genuinely dead agent and
///         flip the deliberate fail-fast-on-open-breaker contract
///         (<c>HalibutCircuitBreakerE2ETests.BreakerForcedOpen_*</c> → Failed).</item>
/// </list>
/// </summary>
public static class TransientFailureClassifier
{
    public static bool IsTransient(Exception ex)
    {
        if (ex is AggregateException aggregate)
            return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTransient);

        if (ex is AgentUnreachableException)
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
