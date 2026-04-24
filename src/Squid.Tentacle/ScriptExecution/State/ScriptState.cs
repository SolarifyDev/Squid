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

    /// <summary>
    /// OS-reported start time of the process referenced by <see cref="ProcessId"/>,
    /// captured at the moment the script was forked. Used alongside
    /// <c>ProcessId</c> in orphan-state detection to defend against PID
    /// recycling on long-running agents.
    ///
    /// <para>Scenario this field exists to defend against: tentacle runs for
    /// several days with a busy script-execution workload; Linux
    /// <c>kernel.pid_max</c> eventually wraps and the OS reassigns a
    /// previously-dead upgrade bash PID to an unrelated process. Without a
    /// start-time cross-check, the orphan-detection predicate in
    /// <c>LocalScriptService.TryBuildStatusFromPersistedLogs</c> would see
    /// a "live" PID and return <c>Running</c>, re-creating the observer-hang
    /// bug the predicate was originally added to solve.</para>
    ///
    /// <para>Nullable for backward-compat: state files written before this
    /// field existed (pre-1.6.x) deserialize to null, and the predicate
    /// falls back to PID-only liveness — same behaviour as pre-field.
    /// Fresh StartScript calls always populate the field.</para>
    /// </summary>
    public DateTimeOffset? ProcessStartedAt { get; set; }

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
