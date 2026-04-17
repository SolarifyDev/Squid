namespace Squid.Tentacle.ScriptExecution.State;

public enum ScriptProgress
{
    Pending,
    Starting,
    Running,
    Complete
}

public sealed class ScriptState
{
    public string TicketId { get; set; } = string.Empty;
    public ScriptProgress Progress { get; set; } = ScriptProgress.Pending;
    public int? ExitCode { get; set; }
    public long NextLogSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ProcessId { get; set; }
    public string? ProcessOwnerSignature { get; set; }

    public bool HasStarted() => Progress is ScriptProgress.Starting or ScriptProgress.Running or ScriptProgress.Complete;

    public bool IsComplete() => Progress == ScriptProgress.Complete;

    public static ScriptState Pending(string ticketId) => new()
    {
        TicketId = ticketId,
        Progress = ScriptProgress.Pending,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
