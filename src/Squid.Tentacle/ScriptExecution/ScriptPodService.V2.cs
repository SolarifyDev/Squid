using System.Diagnostics;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// ScriptService V2 for the script-pod backend, as a thin layer over the V1 core: pod launch, the
/// pending queue and isolation mutex, terminal-result injection, recovery (#552) and the persisted
/// <see cref="ScriptStateStore"/> are reused. V2 adds explicit outcomes and a durable completion tombstone.
///
/// <para>Known limitation inherited from V1: pod logs are drained from an in-memory queue as they are read,
/// so a repeated poll with the same cursor does not replay lines already delivered.</para>
/// </summary>
public partial class ScriptPodService : IScriptServiceV2
{
    private readonly TicketLockStripes _ticketLocks = new();
    private ScriptTombstoneStore? _tombstones;

    internal ScriptTombstoneStore Tombstones
    {
        get => _tombstones ??= ScriptTombstoneStore.ForWorkspace(_tentacleSettings.WorkspacePath);
        set => _tombstones = value;
    }

    /// <summary>Test-only: runs between the two reads of <see cref="IsQueuedOrLaunchingV2"/>, i.e. after the pending-queue read and before the in-flight read.</summary>
    internal Action? LaunchStateReadHookForTests { get; set; }

    /// <summary>Test-only: runs in V2 Cancel after the launch settle wait and before the ticket lock is taken.</summary>
    internal Action? BeforeCancelLockHookForTests { get; set; }

    /// <summary>Test-only: how long V2 Cancel waits for an in-flight launch to settle before it hands the cancel to the launcher.</summary>
    internal TimeSpan LaunchSettleTimeoutForTests { get; set; } = LaunchSettleTimeout;

    public ScriptStatusResponseV2 StartScript(StartScriptCommandV2 command)
    {
        var ticket = command.ScriptTicket;

        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (TryPeekStatusV2(ticket, out var known)) return known!;

            _startsInFlight[ticket.TaskId] = 0;
        }

        // The launch (disk check, pod creation) runs OUTSIDE the ticket lock: the lock is one of 64 shared stripes, so a slow
        // Kubernetes API or workspace would stall every unrelated ticket in the stripe. The in-flight flag, set under the lock, is what
        // makes a concurrent duplicate Start answer Pending instead of launching a second pod.
        try
        {
            var v1 = StartScript(ScriptV2Mapping.ToV1(command));

            return TryPeekStatusV2(ticket, out var started) ? started! : ScriptV2Mapping.FromV1Terminal(v1);
        }
        finally
        {
            _startsInFlight.TryRemove(ticket.TaskId, out _);
            DropUnhonouredCancelRequest(ticket.TaskId);
        }
    }

    public ScriptStatusResponseV2 GetStatus(ScriptStatusRequestV2 request)
    {
        var ticket = request.Ticket;

        // Launch state FIRST, see IsQueuedOrLaunchingV2: a ticket read as "not launching" has either not started or already been tracked.
        var launching = IsQueuedOrLaunchingV2(ticket.TaskId);

        if (IsKnownInMemoryV2(ticket.TaskId) && TryReadLiveStatusV2(request, out var live)) return live!;

        // The pod is being launched: its persisted state says Starting (or does not exist yet), which must not read as lost or unknown.
        if (launching) return ScriptV2Mapping.Pending(ticket, request.LastLogSequence);

        return ReadFinalStatusV2(ticket, request.LastLogSequence);
    }

    public ScriptStatusResponseV2 CancelScript(CancelScriptCommandV2 command)
    {
        var ticket = command.Ticket;
        var cancelled = false;

        // A Cancel that lands while the pod is being launched waits for the launch (bounded) instead of being dropped.
        AwaitLaunchSettledV2(ticket.TaskId);

        BeforeCancelLockHookForTests?.Invoke();

        // Stopping the log stream can take seconds; the cancel under the lock then finds it already stopped.
        if (_scripts.TryGetValue(ticket.TaskId, out var live)) StopLogStream(live);

        try
        {
            lock (_ticketLocks.For(ticket.TaskId))
            {
                var launching = IsQueuedOrLaunchingV2(ticket.TaskId);

                if (_terminalResults.TryGetValue(ticket.TaskId, out var terminal)) return MapTerminalV2(terminal.Response);

                // A launching ticket has no pod yet: the cancel is recorded for its launcher (see CancelScriptCore), so it is never dropped.
                if (_scripts.ContainsKey(ticket.TaskId) || _pendingScripts.ContainsKey(ticket.TaskId) || launching)
                    return CancelLiveV2(command, out cancelled);

                return ReadFinalStatusV2(ticket, command.LastLogSequence);
            }
        }
        finally
        {
            // Freeing the isolation mutex launches the next queued script (pod creation): never under the shared ticket lock.
            if (cancelled) ReleaseMutexAndProcessPending(ticket.TaskId);
        }
    }

    public CompleteScriptResponseV2 CompleteScript(CompleteScriptCommandV2 command)
    {
        var ticket = command.Ticket;
        var completed = false;

        // Waiting for the pod to terminate can take 30s: done before taking the ticket lock, which is shared with unrelated tickets.
        var awaited = AwaitPodTerminationV2(ticket.TaskId);

        try
        {
            lock (_ticketLocks.For(ticket.TaskId))
            {
                var launching = IsQueuedOrLaunchingV2(ticket.TaskId);

                if (IsUnawaitedPodV2(ticket.TaskId, awaited)) return ScriptV2Mapping.NotYetComplete(ScriptV2Mapping.Pending(ticket, command.LastLogSequence));

                if (_terminalResults.ContainsKey(ticket.TaskId) || _scripts.ContainsKey(ticket.TaskId))
                {
                    completed = true;

                    return CompleteLiveV2(command);
                }

                if (launching || _pendingScripts.ContainsKey(ticket.TaskId)) return ScriptV2Mapping.NotYetComplete(ScriptV2Mapping.Pending(ticket, command.LastLogSequence));

                return CompleteNotLiveV2(ticket);
            }
        }
        finally
        {
            if (completed) ReleaseMutexAndProcessPending(ticket.TaskId);
        }
    }

    private const int LaunchSettlePollMilliseconds = 20;

    private static readonly TimeSpan LaunchSettleTimeout = TimeSpan.FromSeconds(30);

    private void AwaitLaunchSettledV2(string ticketId)
    {
        var waited = Stopwatch.StartNew();

        while (_startsInFlight.ContainsKey(ticketId) && waited.Elapsed < LaunchSettleTimeoutForTests)
            Thread.Sleep(LaunchSettlePollMilliseconds);
    }

    // Stops the log stream and waits (bounded) for the tracked pod to terminate. Must run without the ticket lock.
    private ScriptPodContext? AwaitPodTerminationV2(string ticketId)
    {
        if (!_scripts.TryGetValue(ticketId, out var ctx)) return null;

        StopLogStream(ctx);
        _podManager.WaitForPodTermination(ctx.PodName, PodTerminationWait);

        return ctx;
    }

    // A pod that became tracked after the wait above looked is not finalised unawaited: the caller retries and the next Complete waits for it.
    private bool IsUnawaitedPodV2(string ticketId, ScriptPodContext? awaited)
        => !_terminalResults.ContainsKey(ticketId) && _scripts.TryGetValue(ticketId, out var current) && !ReferenceEquals(current, awaited);

    /// <summary>
    /// A ticket launches by moving pending queue -> in-flight set -> tracked pod, adding to each next set before leaving the previous
    /// one, so it is always in at least one of them. These reads are not atomic: scanning in the SAME direction (pending, then in-flight,
    /// then the tracked/persisted state the caller reads afterwards) cannot miss a ticket that moves between them, whereas any other order
    /// can, and a miss reads as lost or unknown (the server re-dispatches). Callers take this snapshot before any other lookup.
    /// </summary>
    private bool IsQueuedOrLaunchingV2(string ticketId)
    {
        var queued = _pendingScripts.ContainsKey(ticketId);

        LaunchStateReadHookForTests?.Invoke();

        return queued || _startsInFlight.ContainsKey(ticketId);
    }

    // In the direction a ticket moves (pending -> tracked -> terminal), see PublishTerminalThenForget: a lock-free read cannot miss a ticket in transit.
    private bool IsKnownInMemoryV2(string ticketId)
    {
        var queued = _pendingScripts.ContainsKey(ticketId);
        var tracked = _scripts.ContainsKey(ticketId);

        KnownInMemoryReadHookForTests?.Invoke();

        return queued || tracked || _terminalResults.ContainsKey(ticketId);
    }

    /// <summary>Test-only: runs between the pending/tracked reads and the terminal read of <see cref="IsKnownInMemoryV2"/>.</summary>
    internal Action? KnownInMemoryReadHookForTests { get; set; }

    private bool TryPeekStatusV2(ScriptTicket ticket, out ScriptStatusResponseV2? status)
    {
        var ticketId = ticket.TaskId;
        var launching = IsQueuedOrLaunchingV2(ticketId);

        status = true switch
        {
            _ when _terminalResults.TryGetValue(ticketId, out var terminal) => MapTerminalV2(terminal.Response),
            _ when _scripts.TryGetValue(ticketId, out var ctx) => ScriptV2Mapping.Running(ticket, new List<ProcessOutput>(), ctx.LogSequence),
            _ when launching || _pendingScripts.ContainsKey(ticketId) => ScriptV2Mapping.Pending(ticket, 0),
            _ => null
        };

        return status != null || TryBuildPersistedStatusV2(ticket, out status) || TryReadTombstoneStatusV2(ticket, out status);
    }

    // A released ticket already ran: a delayed or duplicate Start answers with its recorded result instead of launching another pod.
    private bool TryReadTombstoneStatusV2(ScriptTicket ticket, out ScriptStatusResponseV2? status)
    {
        var tombstone = Tombstones.TryRead(ticket.TaskId);

        status = tombstone != null ? ScriptV2Mapping.FromTombstone(ticket, tombstone, 0) : null;

        return status != null;
    }

    // A terminal result injected for a script the agent lost track of is ResultLost, not a Completed exit code.
    private ScriptStatusResponseV2 MapTerminalV2(ScriptStatusResponse v1)
        => _lostResults.ContainsKey(v1.Ticket.TaskId) ? ScriptV2Mapping.Lost(v1.Ticket, v1.Logs, v1.NextLogSequence) : ScriptV2Mapping.FromV1Terminal(v1);

    // The V1 read may race a concurrent Complete that consumes the entry; if the ticket left memory while we
    // read, V1 answered UnknownResult for it, so discard that and let the persisted/tombstone path decide.
    private bool TryReadLiveStatusV2(ScriptStatusRequestV2 request, out ScriptStatusResponseV2? status)
    {
        var ticket = request.Ticket;
        var v1 = GetStatus(new ScriptStatusRequest(ticket, request.LastLogSequence));

        status = null;

        if (!IsKnownInMemoryV2(ticket.TaskId)) return false;

        status = _pendingScripts.ContainsKey(ticket.TaskId) ? ScriptV2Mapping.Pending(ticket, request.LastLogSequence) : MapLiveV1Status(v1);
        return true;
    }

    private ScriptStatusResponseV2 MapLiveV1Status(ScriptStatusResponse v1)
        => v1.State == ProcessState.Complete ? MapTerminalV2(v1) : ScriptV2Mapping.Running(v1.Ticket, v1.Logs, v1.NextLogSequence);

    private bool TryBuildPersistedStatusV2(ScriptTicket ticket, out ScriptStatusResponseV2? status)
    {
        status = null;

        var store = _stateStoreFactory.Create(Path.Combine(_tentacleSettings.WorkspacePath, ticket.TaskId));

        if (!store.Exists()) return false;

        if (!TryLoadPersistedState(store, out var state))
        {
            status = ScriptV2Mapping.Lost(ticket, new List<ProcessOutput>(), 0);
            return true;
        }

        if (state.Progress == ScriptProgress.Pending) return false;

        status = ClassifyPersistedStateV2(ticket, state);
        return true;
    }

    // Not in memory means no pod is being tracked: a recorded exit code is the result, anything else is lost
    // (recovery already re-attached every pod that still exists).
    private static ScriptStatusResponseV2 ClassifyPersistedStateV2(ScriptTicket ticket, ScriptState state)
    {
        if (state.IsComplete() && state.ExitCode is { } code)
            return ScriptV2Mapping.Completed(ticket, code, new List<ProcessOutput>(), state.NextLogSequence);

        return ScriptV2Mapping.Lost(ticket, new List<ProcessOutput>(), state.NextLogSequence);
    }

    private static bool TryLoadPersistedState(IScriptStateStore store, out ScriptState state)
    {
        try
        {
            state = store.Load();
            return true;
        }
        catch (Exception)
        {
            state = new ScriptState();
            return false;
        }
    }

    // cancelled = this call tore the ticket down (the caller then releases its isolation mutex); false when the launcher took the cancel over.
    private ScriptStatusResponseV2 CancelLiveV2(CancelScriptCommandV2 command, out bool cancelled)
    {
        var ticket = command.Ticket;
        var v1 = CancelScriptCore(new CancelScriptCommand(ticket, command.LastLogSequence), deferredToCaller: true, out var handedToLauncher);

        cancelled = !handedToLauncher;

        if (handedToLauncher) return ScriptV2Mapping.Pending(ticket, command.LastLogSequence);

        // The core published the Canceled result before it stopped tracking the ticket, so a later Cancel/Complete of it still has an answer.
        _terminalResults.TryAdd(ticket.TaskId, (BuildTerminalResponse(ticket.TaskId, ScriptExitCodes.Canceled, v1.Logs), DateTimeOffset.UtcNow));

        return ScriptV2Mapping.FromV1Terminal(v1);
    }

    private CompleteScriptResponseV2 CompleteLiveV2(CompleteScriptCommandV2 command)
    {
        var ticket = command.Ticket;
        var lost = _lostResults.ContainsKey(ticket.TaskId);
        var recorded = false;

        var v1 = CompleteScriptCore(new CompleteScriptCommand(ticket, command.LastLogSequence), response =>
        {
            recorded = true;

            return Tombstones.TryWrite(ScriptV2Mapping.ToTombstone(MapFinalV2(response, lost), DateTimeOffset.UtcNow));
        }, deferredToCaller: true);

        var final = MapFinalV2(v1, lost);

        if (!recorded) Tombstones.Write(ScriptV2Mapping.ToTombstone(final, DateTimeOffset.UtcNow));

        return ScriptV2Mapping.Released(final);
    }

    private static ScriptStatusResponseV2 MapFinalV2(ScriptStatusResponse v1, bool lost)
        => lost ? ScriptV2Mapping.Lost(v1.Ticket, v1.Logs, v1.NextLogSequence) : ScriptV2Mapping.FromV1Terminal(v1);

    private CompleteScriptResponseV2 CompleteNotLiveV2(ScriptTicket ticket)
    {
        if (!TryBuildPersistedStatusV2(ticket, out var persisted))
            return Tombstones.TryRead(ticket.TaskId) != null ? ScriptV2Mapping.AlreadyReleased(ticket) : ScriptV2Mapping.NotKnown(ticket);

        var workDir = Path.Combine(_tentacleSettings.WorkspacePath, ticket.TaskId);

        // Without a durable tombstone the persisted state is the only record of the result: keep it (a repeated Complete retries).
        if (Tombstones.TryWrite(ScriptV2Mapping.ToTombstone(persisted!, DateTimeOffset.UtcNow)))
        {
            DeletePersistedStateIfAny(workDir);
            CleanupWorkspace(workDir);
        }
        else
        {
            StateOnlyWorkspace.Scrub(workDir);
        }

        return ScriptV2Mapping.Released(persisted!);
    }

    private ScriptStatusResponseV2 ReadFinalStatusV2(ScriptTicket ticket, long lastLogSequence)
    {
        if (TryBuildPersistedStatusV2(ticket, out var persisted)) return persisted!;

        var tombstone = Tombstones.TryRead(ticket.TaskId);

        return tombstone != null
            ? ScriptV2Mapping.FromTombstone(ticket, tombstone, lastLogSequence)
            : ScriptV2Mapping.Unknown(ticket, lastLogSequence);
    }
}
