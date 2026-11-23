using Squid.Message.Contracts.Tentacle;

namespace Squid.Message.Models.Deployments.Execution;

/// <summary>
/// The script-service wire version a script was dispatched with. Persisted with the in-flight record so a
/// resume talks to the agent in the SAME dialect the script was started with, whatever the agent
/// advertises by then.
/// </summary>
public enum ScriptProtocolVersion
{
    /// <summary>The original <c>IScriptService</c> contract: every shipped agent speaks it, and records written before version negotiation existed are V1.</summary>
    V1 = 1,

    /// <summary><c>IScriptServiceV2</c>: explicit states, idempotent per ticket.</summary>
    V2 = 2
}

/// <summary>
/// Protocol-neutral lifecycle of a ticket. V1 can only express <see cref="Pending"/>, <see cref="Running"/>
/// and <see cref="Completed"/> (its "unknown ticket" is the magic exit code -1 on a completed status, which
/// V1 never promotes to a state so that V1 behaviour stays exactly as it was); V2 reports all five
/// explicitly, so an unknown ticket and a lost result are never confused with a script that really exited -1.
/// </summary>
public enum AgentScriptState
{
    Pending,
    Running,
    Completed,
    UnknownTicket,
    ResultLost
}

/// <summary>One status answer from an agent, whichever protocol version produced it.</summary>
public sealed record AgentScriptStatus(ScriptTicket Ticket, AgentScriptState State, int ExitCode, List<ProcessOutput> Logs, long NextLogSequence)
{
    /// <summary>True once polling cannot learn anything more: the script finished, or the agent says it never had / lost it.</summary>
    public bool IsTerminal => State is AgentScriptState.Completed or AgentScriptState.UnknownTicket or AgentScriptState.ResultLost;
}

/// <summary>Protocol-neutral outcome of a Complete call. V1 only ever produces <see cref="Completed"/>.</summary>
public enum AgentCompleteOutcome
{
    /// <summary>This call finished the ticket and released it; <see cref="AgentScriptCompletion.Final"/> has the exit code and remaining logs.</summary>
    Completed,

    /// <summary>An earlier Complete already released it (typically: that call's response was lost).</summary>
    AlreadyCompleted,

    /// <summary>The agent has no record of the ticket.</summary>
    UnknownTicket,

    /// <summary>The ticket is still starting, queued or running; <see cref="AgentScriptCompletion.Final"/> is its current status.</summary>
    NotYetComplete
}

public sealed record AgentScriptCompletion(AgentCompleteOutcome Outcome, AgentScriptStatus Final);
