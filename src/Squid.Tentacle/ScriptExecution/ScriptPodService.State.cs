using Serilog;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution.State;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// Generic crash-safe state persistence for the K8s pod-per-script backend.
/// Parallels the protocol-agnostic <see cref="ScriptStateStore"/> writes that
/// <see cref="LocalScriptService"/> performs, so an agent-pod restart can
/// distinguish "script was running, pod still alive" from "script never
/// started" without relying on the in-memory <see cref="_terminalResults"/>
/// cache — which is lost on restart.
///
/// Dual-writes with the existing <see cref="ScriptStateFile"/>:
///   - <see cref="ScriptStateFile"/> keeps K8s-specific fields (pod name,
///     EOS marker token) that <see cref="ScriptRecoveryService"/> needs to
///     reattach to a live pod.
///   - <see cref="ScriptStateStore"/> holds protocol-agnostic progress /
///     exit code / log cursor that <see cref="GetStatus"/> falls back to
///     when the in-memory dictionaries are empty.
/// </summary>
public partial class ScriptPodService
{
    private readonly IScriptStateStoreFactory _stateStoreFactory = new ScriptStateStoreFactory();

    internal void PersistStartingState(string ticketId, string workDir)
    {
        try
        {
            var store = _stateStoreFactory.Create(workDir);
            store.Save(new ScriptState
            {
                TicketId = ticketId,
                Progress = ScriptProgress.Starting,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to persist Starting state for {TicketId}", ticketId);
        }
    }

    internal void PersistRunningState(string ticketId, string workDir)
    {
        try
        {
            var store = _stateStoreFactory.Create(workDir);
            store.Save(new ScriptState
            {
                TicketId = ticketId,
                Progress = ScriptProgress.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to persist Running state for {TicketId}", ticketId);
        }
    }

    internal void PersistCompleteState(string ticketId, string workDir, int exitCode, long nextLogSequence)
    {
        try
        {
            var store = _stateStoreFactory.Create(workDir);
            var existing = store.Exists() ? store.Load() : new ScriptState { TicketId = ticketId };
            existing.Progress = ScriptProgress.Complete;
            existing.ExitCode = exitCode;
            existing.CompletedAt = DateTimeOffset.UtcNow;
            existing.NextLogSequence = nextLogSequence;
            store.Save(existing);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to persist Complete state for {TicketId}", ticketId);
        }
    }

    internal void DeletePersistedStateIfAny(string workDir)
    {
        try
        {
            var store = _stateStoreFactory.Create(workDir);
            if (store.Exists()) store.Delete();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to delete persisted state at {WorkDir}", workDir);
        }
    }

    /// <summary>
    /// On <see cref="StartScript"/> or <see cref="GetStatus"/>, if the in-memory
    /// dictionaries miss but a persisted <see cref="ScriptStateStore"/> says the
    /// script was previously Running/Complete, return a synthesised response
    /// from disk instead of re-launching or treating the ticket as unknown.
    /// Returns null when no persisted state exists or the file is unreadable.
    /// </summary>
    internal ScriptStatusResponse? TryBuildStatusFromPersistedState(ScriptTicket ticket, string workDir)
    {
        try
        {
            var store = _stateStoreFactory.Create(workDir);
            if (!store.Exists()) return null;

            var state = store.Load();
            if (!state.HasStarted()) return null;

            var processState = state.Progress == ScriptProgress.Complete
                ? ProcessState.Complete
                : ProcessState.Running;
            var exitCode = state.ExitCode ?? 0;

            return new ScriptStatusResponse(ticket, processState, exitCode, new List<ProcessOutput>(), state.NextLogSequence);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to build status from persisted state at {WorkDir}", workDir);
            return null;
        }
    }
}
