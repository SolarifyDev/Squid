using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;

namespace Squid.Tentacle.ScriptExecution.V2;

/// <summary>Pure translations between the V1 core's types and the V2 wire types.</summary>
public static class ScriptV2Mapping
{
    public static StartScriptCommand ToV1(StartScriptCommandV2 command)
    {
        return new StartScriptCommand(
            command.ScriptTicket,
            command.ScriptBody,
            command.Isolation,
            command.ScriptIsolationMutexTimeout,
            command.IsolationMutexName,
            command.Arguments,
            command.TaskId,
            command.DurationToWaitForScriptToFinish,
            command.Scripts,
            command.Files.ToArray())
        {
            ScriptSyntax = command.ScriptSyntax,
            TargetNamespace = command.TargetNamespace,
            Labels = command.Labels
        };
    }

    public static ScriptStatusResponseV2 Completed(ScriptTicket ticket, int exitCode, List<ProcessOutput> logs, long next)
        => new(ticket, ScriptStateV2.Completed, exitCode, logs, next);

    public static ScriptStatusResponseV2 Running(ScriptTicket ticket, List<ProcessOutput> logs, long next)
        => new(ticket, ScriptStateV2.Running, 0, logs, next);

    public static ScriptStatusResponseV2 Pending(ScriptTicket ticket, long next)
        => new(ticket, ScriptStateV2.Pending, 0, new List<ProcessOutput>(), next);

    public static ScriptStatusResponseV2 Lost(ScriptTicket ticket, List<ProcessOutput> logs, long next)
        => new(ticket, ScriptStateV2.ResultLost, 0, logs, next);

    public static ScriptStatusResponseV2 Unknown(ScriptTicket ticket, long next)
        => new(ticket, ScriptStateV2.UnknownTicket, 0, new List<ProcessOutput>(), next);

    /// <summary>A V1 terminal response (including drain/admission/launch rejections) is a real agent-asserted result.</summary>
    public static ScriptStatusResponseV2 FromV1Terminal(ScriptStatusResponse v1)
        => Completed(v1.Ticket, v1.ExitCode, v1.Logs, v1.NextLogSequence);

    public static ScriptStatusResponseV2 FromTombstone(ScriptTicket ticket, ScriptTombstone tombstone, long next)
        => new(ticket, tombstone.State, tombstone.State == ScriptStateV2.Completed ? tombstone.ExitCode : 0, new List<ProcessOutput>(), next);

    public static CompleteScriptResponseV2 Released(ScriptStatusResponseV2 final)
        => new(final.Ticket, CompleteScriptOutcomeV2.Completed, final);

    public static CompleteScriptResponseV2 NotYetComplete(ScriptStatusResponseV2 current)
        => new(current.Ticket, CompleteScriptOutcomeV2.NotYetComplete, current);

    public static CompleteScriptResponseV2 AlreadyReleased(ScriptTicket ticket)
        => new(ticket, CompleteScriptOutcomeV2.AlreadyCompleted, null);

    public static CompleteScriptResponseV2 NotKnown(ScriptTicket ticket)
        => new(ticket, CompleteScriptOutcomeV2.UnknownTicket, null);

    public static ScriptTombstone ToTombstone(ScriptStatusResponseV2 final, DateTimeOffset now)
        => new(final.Ticket.TaskId, final.State, final.ExitCode, now);
}
