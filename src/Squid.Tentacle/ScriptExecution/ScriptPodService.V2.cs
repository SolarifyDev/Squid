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

    public ScriptStatusResponseV2 StartScript(StartScriptCommandV2 command)
    {
        var ticket = command.ScriptTicket;

        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (TryPeekStatusV2(ticket, out var known)) return known!;

            var v1 = StartScript(ScriptV2Mapping.ToV1(command));

            return TryPeekStatusV2(ticket, out var started) ? started! : ScriptV2Mapping.FromV1Terminal(v1);
        }
    }

    public ScriptStatusResponseV2 GetStatus(ScriptStatusRequestV2 request)
    {
        var ticket = request.Ticket;

        if (IsKnownInMemoryV2(ticket.TaskId) && TryReadLiveStatusV2(request, out var live)) return live!;

        return TryBuildPersistedStatusV2(ticket, out var persisted) ? persisted! : ScriptV2Mapping.Unknown(ticket, request.LastLogSequence);
    }

    public ScriptStatusResponseV2 CancelScript(CancelScriptCommandV2 command)
    {
        var ticket = command.Ticket;

        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (_terminalResults.TryGetValue(ticket.TaskId, out var terminal)) return ScriptV2Mapping.FromV1Terminal(terminal.Response);

            if (_scripts.ContainsKey(ticket.TaskId) || _pendingScripts.ContainsKey(ticket.TaskId)) return CancelLiveV2(command);

            return ReadFinalStatusV2(ticket, command.LastLogSequence);
        }
    }

    public CompleteScriptResponseV2 CompleteScript(CompleteScriptCommandV2 command)
    {
        var ticket = command.Ticket;

        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (_terminalResults.ContainsKey(ticket.TaskId) || _scripts.ContainsKey(ticket.TaskId)) return CompleteLiveV2(command);

            if (_pendingScripts.ContainsKey(ticket.TaskId)) return ScriptV2Mapping.NotYetComplete(ScriptV2Mapping.Pending(ticket, command.LastLogSequence));

            return CompleteNotLiveV2(ticket);
        }
    }

    private bool IsKnownInMemoryV2(string ticketId)
        => _terminalResults.ContainsKey(ticketId) || _pendingScripts.ContainsKey(ticketId) || _scripts.ContainsKey(ticketId);

    private bool TryPeekStatusV2(ScriptTicket ticket, out ScriptStatusResponseV2? status)
    {
        var ticketId = ticket.TaskId;

        status = true switch
        {
            _ when _terminalResults.TryGetValue(ticketId, out var terminal) => ScriptV2Mapping.FromV1Terminal(terminal.Response),
            _ when _scripts.TryGetValue(ticketId, out var ctx) => ScriptV2Mapping.Running(ticket, new List<ProcessOutput>(), ctx.LogSequence),
            _ when _pendingScripts.ContainsKey(ticketId) => ScriptV2Mapping.Pending(ticket, 0),
            _ => null
        };

        return status != null || TryBuildPersistedStatusV2(ticket, out status);
    }

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

    private static ScriptStatusResponseV2 MapLiveV1Status(ScriptStatusResponse v1)
        => v1.State == ProcessState.Complete ? ScriptV2Mapping.FromV1Terminal(v1) : ScriptV2Mapping.Running(v1.Ticket, v1.Logs, v1.NextLogSequence);

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

    private ScriptStatusResponseV2 CancelLiveV2(CancelScriptCommandV2 command)
    {
        var ticket = command.Ticket;
        var v1 = CancelScript(new CancelScriptCommand(ticket, command.LastLogSequence));

        // V1 releases the pod and workspace on cancel; keep the outcome so a later Cancel/Complete of this ticket still has an answer.
        InjectTerminalResult(ticket.TaskId, ScriptExitCodes.Canceled, v1.Logs);

        return ScriptV2Mapping.FromV1Terminal(v1);
    }

    private CompleteScriptResponseV2 CompleteLiveV2(CompleteScriptCommandV2 command)
    {
        var ticket = command.Ticket;
        var final = ScriptV2Mapping.FromV1Terminal(CompleteScript(new CompleteScriptCommand(ticket, command.LastLogSequence)));

        Tombstones.Write(ScriptV2Mapping.ToTombstone(final, DateTimeOffset.UtcNow));

        return ScriptV2Mapping.Released(final);
    }

    private CompleteScriptResponseV2 CompleteNotLiveV2(ScriptTicket ticket)
    {
        if (!TryBuildPersistedStatusV2(ticket, out var persisted))
            return Tombstones.TryRead(ticket.TaskId) != null ? ScriptV2Mapping.AlreadyReleased(ticket) : ScriptV2Mapping.NotKnown(ticket);

        var workDir = Path.Combine(_tentacleSettings.WorkspacePath, ticket.TaskId);

        Tombstones.Write(ScriptV2Mapping.ToTombstone(persisted!, DateTimeOffset.UtcNow));
        DeletePersistedStateIfAny(workDir);
        CleanupWorkspace(workDir);

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
