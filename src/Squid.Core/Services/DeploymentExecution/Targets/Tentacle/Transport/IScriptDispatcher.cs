using Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// The protocol-version-specific part of dispatching a script to a Tentacle agent. Everything that is
/// the same whichever script-service version the agent speaks stays in
/// <see cref="HalibutMachineExecutionStrategy"/> and is shared by every dispatcher: endpoint parsing,
/// circuit breaker, polling-work admission, ticket generation, the machine-scoped isolation mutex name,
/// the in-flight record-before-RPC / clear / re-attach protocol, RPC retry wrapping, the observer and
/// the remote-cancel decision.
///
/// <para>Only these vary per protocol version, so a new version is a sibling implementation (under
/// <c>Dispatchers/</c>) and the strategy never changes: opening the client, turning a
/// <see cref="ScriptDispatchSpec"/> into that version's start call, and recognising the agent's
/// "I don't know this ticket" answer. Poll / cancel / complete are reached through the returned
/// <see cref="IAsyncScriptService"/>, which is what the shared observer and retry wrapper speak; a version
/// with a different wire contract exposes itself through an adapter implementing that interface.</para>
///
/// <para>Deliberately narrow (ISP): capability negotiation that picks WHICH dispatcher serves a machine is
/// a separate concern and does not belong on this interface.</para>
/// </summary>
public interface IScriptDispatcher : IScopedDependency
{
    /// <summary>Opens the per-dispatch client for the agent. No I/O is performed.</summary>
    IAsyncScriptService CreateClient(ServiceEndPoint endpoint);

    /// <summary>
    /// Validates the spec and builds this protocol version's start call WITHOUT performing any I/O, so an
    /// unsupported spec fails before the strategy records an in-flight pointer or probes the agent. The
    /// returned delegate performs the start RPC on <paramref name="client"/> (the raw client — StartScript
    /// is never retried) and returns the agent's initial status.
    /// </summary>
    Func<Task<ScriptStatusResponse>> PrepareStart(IAsyncScriptService client, ScriptDispatchSpec spec);

    /// <summary>
    /// Whether a <c>GetStatus</c> probe says the agent does NOT hold the ticket (or gave no answer), so the
    /// strategy dispatches fresh. Every other answer means the agent demonstrably holds the script and the
    /// strategy re-attaches instead of duplicating it.
    /// </summary>
    bool IsUnknownTicket(ScriptStatusResponse probe);
}
