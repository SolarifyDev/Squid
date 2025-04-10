namespace Squid.Core.Commands.Tentacle;

public class CompleteScriptCommand
{
    public CompleteScriptCommand(ScriptTicket ticket, long lastLogSequence)
    {
        Ticket = ticket;
        LastLogSequence = lastLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public long LastLogSequence { get; }
}