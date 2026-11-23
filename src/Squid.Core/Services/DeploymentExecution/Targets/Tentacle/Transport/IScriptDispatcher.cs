using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// The protocol-version-specific part of dispatching a script to a Tentacle agent. Everything that is
/// the same whichever script-service version the agent speaks stays in
/// <see cref="HalibutMachineExecutionStrategy"/> and is shared by every dispatcher: endpoint parsing,
/// circuit breaker, polling-work admission, ticket generation, the machine-scoped isolation mutex name,
/// the in-flight record-before-RPC / clear / re-attach protocol, the observer's liveness probe and
/// cancel decision.
///
/// <para>A dispatcher is one protocol version: it opens a per-dispatch <see cref="IScriptAgentSession"/>
/// that speaks that version's wire contract (and carries that version's retry rules) while exposing the
/// protocol-neutral <see cref="AgentScriptStatus"/>. A new version is a sibling implementation under
/// <c>Dispatchers/</c>; the strategy never changes.</para>
///
/// <para>Deliberately narrow (ISP): capability negotiation that picks WHICH dispatcher serves a machine is
/// a separate concern (<see cref="IScriptDispatcherSelector"/>); never resolve a bare
/// <c>IScriptDispatcher</c> — with several implementations registered the choice would be arbitrary.</para>
/// </summary>
public interface IScriptDispatcher : IScopedDependency
{
    /// <summary>The wire version this dispatcher speaks; the selector indexes dispatchers by it and it is persisted with the in-flight record.</summary>
    ScriptProtocolVersion Version { get; }

    /// <summary>Opens the per-dispatch view of the agent. No I/O is performed.</summary>
    IScriptAgentSession Open(ServiceEndPoint endpoint, ScriptSessionContext context);
}

/// <summary>What the strategy hands a dispatcher so the session can apply the shared retry budgets and observe the script.</summary>
public sealed record ScriptSessionContext(RpcRetryHandler Retry, TimeSpan ScriptTimeout, CancellationToken Ct, IHalibutScriptObserver Observer);

/// <summary>
/// One dispatch's protocol-bound conversation with an agent: validate and start the script, probe a
/// recorded ticket, observe it to completion. Poll / cancel / complete go through the retry-decorated
/// client inside the session, with the same budgets whichever version it speaks.
/// </summary>
public interface IScriptAgentSession
{
    ScriptProtocolVersion Version { get; }

    /// <summary>
    /// Validates the spec and builds this version's start call WITHOUT performing any I/O, so an
    /// unsupported spec fails before the strategy records an in-flight pointer or probes the agent. The
    /// returned delegate performs the start RPC and returns the agent's initial status.
    /// </summary>
    Func<Task<AgentScriptStatus>> PrepareStart(ScriptDispatchSpec spec);

    /// <summary>Asks the agent what it knows about a ticket recorded by an earlier run.</summary>
    Task<AgentScriptStatus> ProbeAsync(ScriptTicket ticket);

    /// <summary>
    /// Whether a probe says the agent does NOT hold the ticket (or gave no answer), so the strategy
    /// dispatches fresh. Every other answer means the agent demonstrably holds the script (or its
    /// outcome) and the strategy re-attaches instead of duplicating it.
    /// </summary>
    bool IsUnknownTicket(AgentScriptStatus probe);

    /// <summary>
    /// Whether a probe that throws a NON-transient error means "the agent doesn't have the ticket" (V1: no
    /// explicit unknown answer exists, so today's fall-back to a fresh dispatch is kept). False when the
    /// protocol answers unknown explicitly, so an error is never evidence that the script did not run.
    /// </summary>
    bool ProbeFailureMeansUnknownTicket { get; }

    /// <summary>Observes the script to completion (or to the explicit lost / unknown outcome) and returns its result.</summary>
    Task<ScriptExecutionResult> ObserveAsync(ScriptExecutionRequest request, ServiceEndPoint endpoint, ScriptTicket ticket, AgentScriptStatus initial);
}
