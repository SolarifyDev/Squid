namespace Squid.Message.Contracts.Tentacle.V2;

/// <summary>
/// ScriptService V2 wire contract. Halibut routes by interface name (<c>typeof(T).Name</c>), so
/// <c>IScriptServiceV2</c> is a service distinct from <c>IScriptService</c>: both can be registered on
/// one agent, and an agent that only registers V1 answers a V2 call with a "no such service" fault.
///
/// <para>Every method is idempotent per client-supplied ticket and every outcome is explicit in
/// <see cref="ScriptStatusResponseV2.State"/> — no magic exit codes.</para>
/// </summary>
public interface IScriptServiceV2
{
    /// <summary>
    /// Starts the script identified by <see cref="StartScriptCommandV2.ScriptTicket"/>. Idempotent:
    /// a repeated Start for a ticket the agent already knows returns that script's current status
    /// and NEVER runs it again, whatever the repeated command contains.
    /// </summary>
    ScriptStatusResponseV2 StartScript(StartScriptCommandV2 command);

    /// <summary>
    /// Returns the current status plus the logs after <see cref="ScriptStatusRequestV2.LastLogSequence"/>.
    /// Pass the returned <see cref="ScriptStatusResponseV2.NextLogSequence"/> back as the next cursor.
    /// An unknown ticket yields <see cref="ScriptStateV2.UnknownTicket"/>.
    /// </summary>
    ScriptStatusResponseV2 GetStatus(ScriptStatusRequestV2 request);

    /// <summary>
    /// Requests cancellation and returns the status. Idempotent: cancelling a finished script returns
    /// its final status; an unknown ticket yields <see cref="ScriptStateV2.UnknownTicket"/>.
    /// </summary>
    ScriptStatusResponseV2 CancelScript(CancelScriptCommandV2 command);

    /// <summary>
    /// Finishes the script: returns the remaining logs and releases the agent's resources for the
    /// ticket. Idempotent: a second Complete, or one for an unknown ticket, returns an explicit
    /// <see cref="CompleteScriptOutcomeV2"/> instead of a fabricated exit code.
    /// A ticket that is still starting, queued or running returns <see cref="CompleteScriptOutcomeV2.NotYetComplete"/> with its current status; Complete never throws for it.
    /// </summary>
    CompleteScriptResponseV2 CompleteScript(CompleteScriptCommandV2 command);
}
