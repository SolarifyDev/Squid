using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Script;

/// <summary>
/// The failed <see cref="ScriptExecutionResult"/> for a script whose outcome the agent can no longer tell
/// us (V2 <see cref="AgentScriptState.ResultLost"/>, or a ticket the agent accepted and then no longer
/// knows). The script MAY have run, so the deployment must neither report success nor silently run it
/// again: it fails the action with a message that says exactly that, so the operator decides whether the
/// target already has the change. Failing is the conservative choice — a silent re-run risks a
/// non-idempotent script executing twice, a silent success hides an unverified change; both are worse than
/// a clear failure the operator can act on.
/// </summary>
public static class LostScriptResult
{
    public static ScriptExecutionResult Build(AgentScriptState state, string machineName, ScriptTicket ticket, List<string> priorLogLines)
    {
        var message = Describe(state, machineName, ticket);
        var lines = new List<string>(priorLogLines) { message };

        return new ScriptExecutionResult
        {
            Success = false,
            ExitCode = ScriptExitCodes.UnknownResult,
            LogLines = lines,
            StderrLines = new List<string> { message }
        };
    }

    internal static string Describe(AgentScriptState state, string machineName, ScriptTicket ticket) => state switch
    {
        AgentScriptState.ResultLost =>
            $"Agent '{machineName}' lost the result of script {ticket.TaskId}: it knew the script but no longer has its outcome (for example the agent restarted while it ran). " +
            "The script may have run, so Squid did NOT run it again. Check the target to see whether this step's changes were applied, then re-run the deployment if needed.",
        _ =>
            $"Agent '{machineName}' no longer knows script {ticket.TaskId} that it had accepted, so its outcome is unknown. " +
            "The script may have run, so Squid did NOT run it again. Check the target to see whether this step's changes were applied, then re-run the deployment if needed."
    };
}
