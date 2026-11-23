namespace Squid.Message.Contracts.Tentacle.V2;

/// <summary>
/// Lifecycle of a ticket as the agent knows it. Numeric values are wire-pinned: never renumber or reuse.
/// <c>Unspecified = 0</c> is what a missing field deserialises to; a correct agent never sends it and a
/// consumer must treat it as a protocol error rather than guess.
/// </summary>
public enum ScriptStateV2
{
    Unspecified = 0,
    Pending = 1,
    Running = 2,

    /// <summary>The script finished; <see cref="ScriptStatusResponseV2.ExitCode"/> is its real exit code.</summary>
    Completed = 3,

    /// <summary>The agent has never seen this ticket. The script did not run here (or its record was already released).</summary>
    UnknownTicket = 4,

    /// <summary>The agent knew the ticket but lost the outcome (e.g. restarted mid-run). No exit code exists.</summary>
    ResultLost = 5
}

/// <summary>Wire-pinned outcome of <see cref="IScriptServiceV2.CompleteScript"/>.</summary>
public enum CompleteScriptOutcomeV2
{
    Unspecified = 0,

    /// <summary>This call finished the ticket and released its resources.</summary>
    Completed = 1,

    /// <summary>An earlier Complete already released the ticket.</summary>
    AlreadyCompleted = 2,

    /// <summary>The agent has no record of the ticket.</summary>
    UnknownTicket = 3,

    /// <summary>
    /// The ticket exists but is still starting, queued or running (including under a previous agent process), so it cannot be
    /// released. <see cref="CompleteScriptResponseV2.FinalStatus"/> carries its current status; poll or cancel, then Complete again.
    /// </summary>
    NotYetComplete = 4
}

/// <summary>
/// Property-based on purpose (no long positional constructor): adjacent same-typed parameters are an
/// arg-position trap (the #427 mutex-name-in-taskId bug), so every field is set by name.
/// </summary>
public class StartScriptCommandV2
{
    private readonly ScriptTicket _scriptTicket;

    /// <summary>Client-chosen idempotency key.</summary>
    public required ScriptTicket ScriptTicket { get => _scriptTicket; init => _scriptTicket = value ?? throw new ArgumentNullException(nameof(ScriptTicket)); }

    public string ScriptBody { get; init; } = string.Empty;

    public ScriptIsolationLevel Isolation { get; init; }

    /// <summary>Default 30 minutes: what the server sends for V1 deployments today (<c>HalibutSetting.Polling.ScriptTimeoutMinutes</c>). A zero timeout would make the agent's mutex acquire fail even for NoIsolation.</summary>
    public TimeSpan ScriptIsolationMutexTimeout { get; init; } = DefaultScriptIsolationMutexTimeout;

    public static readonly TimeSpan DefaultScriptIsolationMutexTimeout = TimeSpan.FromMinutes(30);

    public string? IsolationMutexName { get; init; }

    public string[] Arguments { get; init; } = Array.Empty<string>();

    public string? TaskId { get; init; }

    public TimeSpan DurationToWaitForScriptToFinish { get; init; }

    public ScriptType ScriptSyntax { get; init; } = ScriptType.Bash;

    public string? TargetNamespace { get; init; }

    public Dictionary<string, string>? Labels { get; init; }

    public Dictionary<ScriptType, string> Scripts { get; init; } = new();

    public List<ScriptFile> Files { get; init; } = new();
}

public class ScriptStatusRequestV2
{
    public ScriptStatusRequestV2(ScriptTicket ticket, long lastLogSequence)
    {
        Ticket = ticket;
        LastLogSequence = lastLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public long LastLogSequence { get; }
}

public class ScriptStatusResponseV2
{
    public ScriptStatusResponseV2(ScriptTicket ticket, ScriptStateV2 state, int exitCode, List<ProcessOutput> logs, long nextLogSequence)
    {
        Ticket = ticket;
        State = state;
        ExitCode = exitCode;
        Logs = logs;
        NextLogSequence = nextLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public ScriptStateV2 State { get; }

    /// <summary>Meaningful only when <see cref="State"/> is <see cref="ScriptStateV2.Completed"/>; 0 and to be ignored otherwise.</summary>
    public int ExitCode { get; }

    public List<ProcessOutput> Logs { get; }

    /// <summary>The cursor to send as <see cref="ScriptStatusRequestV2.LastLogSequence"/> on the next poll.</summary>
    public long NextLogSequence { get; }

    public bool HasExitCode() => State == ScriptStateV2.Completed;
}

public class CancelScriptCommandV2
{
    public CancelScriptCommandV2(ScriptTicket ticket, long lastLogSequence)
    {
        Ticket = ticket;
        LastLogSequence = lastLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public long LastLogSequence { get; }
}

public class CompleteScriptCommandV2
{
    public CompleteScriptCommandV2(ScriptTicket ticket, long lastLogSequence)
    {
        Ticket = ticket;
        LastLogSequence = lastLogSequence;
    }

    public ScriptTicket Ticket { get; }

    public long LastLogSequence { get; }
}

public class CompleteScriptResponseV2
{
    public CompleteScriptResponseV2(ScriptTicket ticket, CompleteScriptOutcomeV2 outcome, ScriptStatusResponseV2? finalStatus)
    {
        Ticket = ticket;
        Outcome = outcome;
        FinalStatus = finalStatus;
    }

    public ScriptTicket Ticket { get; }

    public CompleteScriptOutcomeV2 Outcome { get; }

    /// <summary>Final status with the remaining logs for <see cref="CompleteScriptOutcomeV2.Completed"/>; the current status for <see cref="CompleteScriptOutcomeV2.NotYetComplete"/>; null otherwise.</summary>
    public ScriptStatusResponseV2? FinalStatus { get; }
}
