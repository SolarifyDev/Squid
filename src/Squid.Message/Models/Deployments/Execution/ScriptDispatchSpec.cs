using Squid.Message.Contracts.Tentacle;

namespace Squid.Message.Models.Deployments.Execution;

/// <summary>
/// Protocol-neutral description of ONE script to start on a Tentacle agent: everything the deployment
/// strategy decides (ticket, body, files, timeout, machine-scoped isolation mutex name, correlation
/// task id, syntax, namespace) and nothing about how a given script-service protocol version spells it
/// on the wire. A script dispatcher turns it into that version's start command.
/// </summary>
public sealed record ScriptDispatchSpec
{
    public ScriptTicket Ticket { get; init; }

    public string ScriptBody { get; init; }

    public ScriptFile[] Files { get; init; }

    public TimeSpan Timeout { get; init; }

    /// <summary>Machine-scoped lock name every FullIsolation script on the machine serialises behind.</summary>
    public string IsolationMutexName { get; init; }

    /// <summary>Server task id, carried to the agent purely for correlation.</summary>
    public int ServerTaskId { get; init; }

    public ScriptSyntax Syntax { get; init; }

    public string TargetNamespace { get; init; }
}
