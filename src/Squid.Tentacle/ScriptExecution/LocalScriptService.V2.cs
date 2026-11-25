using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Serilog;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// ScriptService V2 over the SAME core as V1: the state store, sequenced log, isolation mutex, persisted
/// exit code, drain marker and orphan-PID liveness are all reused; V2 only adds explicit outcomes
/// (UnknownTicket / ResultLost / AlreadyCompleted) and per-ticket idempotency on top.
/// </summary>
public partial class LocalScriptService
{
    public ScriptStatusResponseV2 StartScript(StartScriptCommandV2 command)
    {
        var ticket = command.ScriptTicket;
        var ticketId = ticket.TaskId;

        if (TryClaimStartOrReadKnown(ticket, out var known)) return known!;

        try
        {
            var v1 = StartScript(ScriptV2Mapping.ToV1(command));

            return TryBuildStatusV2(ticket, 0, out var started) ? started! : ScriptV2Mapping.FromV1Terminal(v1);
        }
        finally
        {
            _startsInFlight.TryRemove(ticketId, out _);
        }
    }

    public ScriptStatusResponseV2 GetStatus(ScriptStatusRequestV2 request)
    {
        // Read the flag BEFORE the state lookup: a Start that writes its state and then leaves _startsInFlight between the two
        // reads would otherwise look unknown, and UnknownTicket makes the server re-dispatch (the script runs twice).
        var startInFlight = _startsInFlight.ContainsKey(request.Ticket.TaskId);

        if (TryBuildStatusV2(request.Ticket, request.LastLogSequence, out var status)) return status!;

        // A Start still waiting for the isolation mutex has no state file yet.
        if (startInFlight) return ScriptV2Mapping.Pending(request.Ticket, request.LastLogSequence);

        return ReadReleasedStatusV2(request.Ticket, request.LastLogSequence);
    }

    public ScriptStatusResponseV2 CancelScript(CancelScriptCommandV2 command)
    {
        var ticket = command.Ticket;

        if (_startsInFlight.ContainsKey(ticket.TaskId))
        {
            _cancellationRegistry.Cancel(ticket);

            return ScriptV2Mapping.Pending(ticket, command.LastLogSequence);
        }

        // Kill + bounded drain happen OUTSIDE the ticket lock: the lock is one of 64 shared stripes, so waiting on a
        // process or its output pipe while holding it would stall unrelated tickets.
        if (_scripts.TryGetValue(ticket.TaskId, out var live) && ShouldIssueCancelV2(live)) IssueCancelV2(ticket, live);

        DrainOutputOutsideTicketLock(ticket);

        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (_scripts.TryGetValue(ticket.TaskId, out var running)) return CancelRunningV2(ticket, command.LastLogSequence, running);

            return ReadFinalStatusV2(ticket, command.LastLogSequence);
        }
    }

    public CompleteScriptResponseV2 CompleteScript(CompleteScriptCommandV2 command)
    {
        var ticket = command.Ticket;

        if (_scripts.TryGetValue(ticket.TaskId, out var live)) AwaitExitAndOutputV2(live);

        lock (_ticketLocks.For(ticket.TaskId))
        {
            // Flag first for the same reason as GetStatus: a Start finishing between the _scripts and _startsInFlight reads must not look absent.
            var startInFlight = _startsInFlight.ContainsKey(ticket.TaskId);

            CompleteStateLookupHookForTests?.Invoke();

            if (_scripts.TryRemove(ticket.TaskId, out var running)) return CompleteRunningV2(ticket, command.LastLogSequence, running);

            if (startInFlight) return ScriptV2Mapping.NotYetComplete(ScriptV2Mapping.Pending(ticket, command.LastLogSequence));

            return CompleteNotRunningV2(ticket, command.LastLogSequence);
        }
    }

    private bool TryClaimStartOrReadKnown(ScriptTicket ticket, out ScriptStatusResponseV2? known)
    {
        DrainOutputOutsideTicketLock(ticket);

        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (TryBuildStatusV2(ticket, 0, out known, drain: false)) return true;

            if (TryReadTombstoneStatusV2(ticket, out known)) return true;

            if (_startsInFlight.TryAdd(ticket.TaskId, 0)) return false;

            known = ScriptV2Mapping.Pending(ticket, 0);
            return true;
        }
    }

    // A released ticket already ran: a delayed or duplicate Start answers with its recorded result instead of running it again.
    private bool TryReadTombstoneStatusV2(ScriptTicket ticket, out ScriptStatusResponseV2? status)
    {
        var tombstone = _tombstones.TryRead(ticket.TaskId);

        status = tombstone != null ? ScriptV2Mapping.FromTombstone(ticket, tombstone, 0) : null;

        return status != null;
    }

    // The ONE place the bounded output drain is done for a ticket that is about to be handled under its lock: the lock is one
    // of 64 shared stripes, so the drain (up to the drain budget while a background child holds stdout) must come first.
    // Under the lock BuildStatusV2 runs with drain:false, so a script that exits after this call is reported as still
    // running until the next poll instead of stalling every ticket in the stripe.
    private void DrainOutputOutsideTicketLock(ScriptTicket ticket)
    {
        if (_scripts.TryGetValue(ticket.TaskId, out var running)) AwaitOutputDrained(running);

        AfterPreLockDrainHookForTests?.Invoke();
    }

    private bool TryBuildStatusV2(ScriptTicket ticket, long lastLogSequence, out ScriptStatusResponseV2? status, bool drain = true)
    {
        if (_scripts.TryGetValue(ticket.TaskId, out var running))
        {
            status = BuildStatusV2(ticket, running, lastLogSequence, drain);
            return true;
        }

        return TryBuildPersistedStatusV2(ticket, lastLogSequence, out status);
    }

    private ScriptStatusResponseV2 BuildStatusV2(ScriptTicket ticket, RunningScript running, long lastLogSequence, bool drain = true)
    {
        // Read before draining: see BuildStatus.
        var exited = running.Process.HasExited;

        if (drain) AwaitOutputDrained(running);

        // Without a drain, "exited" is only trustworthy once the output is complete; otherwise the final lines could be missing from a Complete.
        if (!drain && !running.OutputDrained) exited = false;

        var (logs, next) = ReadLogsAndCursor(running, lastLogSequence - 1);

        StatusLogsReadHookForTests?.Invoke();

        if (running.V2CancelExitCode is { } canceled) return ScriptV2Mapping.Completed(ticket, canceled, logs, next);

        return exited
            ? ScriptV2Mapping.Completed(ticket, running.Process.ExitCode, logs, next)
            : ScriptV2Mapping.Running(ticket, logs, next);
    }

    private bool TryBuildPersistedStatusV2(ScriptTicket ticket, long lastLogSequence, out ScriptStatusResponseV2? status)
    {
        status = null;

        var workDir = ResolveWorkDir(ticket.TaskId);
        var store = _stateStoreFactory.Create(workDir);

        if (!store.Exists()) return false;

        if (!TryLoadState(store, out var state))
        {
            status = ScriptV2Mapping.Lost(ticket, new List<ProcessOutput>(), lastLogSequence);
            return true;
        }

        if (state.Progress == ScriptProgress.Pending) return false;

        var (logs, derivedNext) = ReadLogsAndCursor(Path.Combine(workDir, "output.log"), lastLogSequence - 1);

        status = ClassifyPersistedState(ticket, state, logs, derivedNext);
        return true;
    }

    private ScriptStatusResponseV2 ClassifyPersistedState(ScriptTicket ticket, ScriptState state, List<ProcessOutput> logs, long derivedNext)
    {
        if (state.IsComplete())
        {
            var finalNext = state.NextLogSequence > 0 ? state.NextLogSequence : derivedNext;

            return state.ExitCode is { } code
                ? ScriptV2Mapping.Completed(ticket, code, logs, finalNext)
                : ScriptV2Mapping.Lost(ticket, logs, finalNext);
        }

        if (_startsInFlight.ContainsKey(ticket.TaskId)) return ScriptV2Mapping.Pending(ticket, derivedNext);

        return IsRecordedProcessAlive(state)
            ? ScriptV2Mapping.Running(ticket, logs, derivedNext)
            : ScriptV2Mapping.Lost(ticket, logs, derivedNext);
    }

    private static bool IsRecordedProcessAlive(ScriptState state)
        => state.ProcessId is > 0 && IsSameProcessAlive(state.ProcessId.Value, state.ProcessStartedAt);

    private static bool TryLoadState(IScriptStateStore store, out ScriptState state)
    {
        try
        {
            state = store.Load();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Script state at {WorkDir} is unreadable; reporting the result as lost", store.WorkspacePath);
            state = new ScriptState();
            return false;
        }
    }

    private static bool ShouldIssueCancelV2(RunningScript running) => !running.V2CancelIssued && !running.V2Canceled && IsProcessRunning(running);

    // Kills the process and waits (bounded) for it and its output to go away. Must run without the ticket lock.
    private void IssueCancelV2(ScriptTicket ticket, RunningScript running)
    {
        _cancellationRegistry.Cancel(ticket);

        // Flag BEFORE the kill so the exit callback does not record the kill signal's exit code.
        MarkCancelRequested(running);
        running.V2CancelIssued = true;

        try
        {
            running.Process.Kill(entireProcessTree: true);

            if (!running.Process.HasExited)
                running.Process.WaitForExit(TimeSpan.FromSeconds(5));

            AwaitOutputDrained(running);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill process for ticket {TicketId}", ticket.TaskId);
        }
    }

    // Under the ticket lock: only records the outcome of a cancel already carried out by IssueCancelV2 (no waiting).
    private ScriptStatusResponseV2 CancelRunningV2(ScriptTicket ticket, long lastLogSequence, RunningScript running)
    {
        if (running.V2CancelIssued && !running.V2Canceled)
        {
            running.V2Canceled = true;

            FinalizeState(running, ScriptExitCodes.Canceled);
        }

        return BuildStatusV2(ticket, running, lastLogSequence, drain: false);
    }

    // Outside the ticket lock: lets a finishing script exit and its output drain (both bounded) before the lock is taken.
    private static void AwaitExitAndOutputV2(RunningScript running)
    {
        try
        {
            if (!running.Process.HasExited)
                running.Process.WaitForExit(TimeSpan.FromSeconds(30));

            AwaitOutputDrained(running);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Skipped pre-complete wait for {WorkDir}: process already released", running.WorkDir);
        }
    }

    private CompleteScriptResponseV2 CompleteRunningV2(ScriptTicket ticket, long lastLogSequence, RunningScript running)
    {
        var v1 = CompleteRunning(ticket, lastLogSequence, running, running.V2CancelWins ? ScriptExitCodes.Canceled : null, exitCode => TryWriteTombstone(ticket, ScriptStateV2.Completed, exitCode), TimeSpan.Zero);

        _cancellationRegistry.Cleanup(ticket);

        return ScriptV2Mapping.Released(ScriptV2Mapping.FromV1Terminal(v1));
    }

    private CompleteScriptResponseV2 CompleteNotRunningV2(ScriptTicket ticket, long lastLogSequence)
    {
        if (TryBuildPersistedStatusV2(ticket, lastLogSequence, out var persisted)) return ReleasePersistedV2(ticket, persisted!);

        return _tombstones.TryRead(ticket.TaskId) != null ? ScriptV2Mapping.AlreadyReleased(ticket) : ScriptV2Mapping.NotKnown(ticket);
    }

    private CompleteScriptResponseV2 ReleasePersistedV2(ScriptTicket ticket, ScriptStatusResponseV2 persisted)
    {
        if (persisted.State is ScriptStateV2.Running or ScriptStateV2.Pending) return ScriptV2Mapping.NotYetComplete(persisted);

        // Without a durable tombstone the persisted state is the only record of the result: keep it (a repeated Complete retries).
        if (TryWriteTombstone(ticket, persisted.State, persisted.ExitCode))
            CleanupWorkDir(ResolveWorkDir(ticket.TaskId));
        else
            StateOnlyWorkspace.Scrub(ResolveWorkDir(ticket.TaskId));

        _cancellationRegistry.Cleanup(ticket);

        return ScriptV2Mapping.Released(persisted);
    }

    // A released ticket is answered from its tombstone; Unknown therefore means never seen or expired from retention.
    private ScriptStatusResponseV2 ReadReleasedStatusV2(ScriptTicket ticket, long lastLogSequence)
    {
        var tombstone = _tombstones.TryRead(ticket.TaskId);

        return tombstone != null
            ? ScriptV2Mapping.FromTombstone(ticket, tombstone, lastLogSequence)
            : ScriptV2Mapping.Unknown(ticket, lastLogSequence);
    }

    private ScriptStatusResponseV2 ReadFinalStatusV2(ScriptTicket ticket, long lastLogSequence)
    {
        return TryBuildPersistedStatusV2(ticket, lastLogSequence, out var persisted) ? persisted! : ReadReleasedStatusV2(ticket, lastLogSequence);
    }

    private bool TryWriteTombstone(ScriptTicket ticket, ScriptStateV2 state, int exitCode)
        => _tombstones.TryWrite(new ScriptTombstone(ticket.TaskId, state, exitCode, DateTimeOffset.UtcNow));
}
