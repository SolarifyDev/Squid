using System.Collections.Concurrent;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Serilog;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService : IScriptService, ITentacleScriptBackend
{
    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly KubernetesPodManager _podManager;
    private readonly ConcurrentDictionary<string, ScriptPodContext> _scripts = new();
    private readonly ConcurrentDictionary<string, ScriptStatusResponse> _terminalResults = new();
    private readonly ConcurrentDictionary<string, IDisposable> _mutexLocks = new();
    private readonly ConcurrentDictionary<string, StartScriptCommand> _pendingScripts = new();
    private readonly ScriptIsolationMutex _isolationMutex = new();

    public ScriptPodService(
        TentacleSettings tentacleSettings,
        KubernetesSettings kubernetesSettings,
        KubernetesPodManager podManager)
    {
        _tentacleSettings = tentacleSettings;
        _kubernetesSettings = kubernetesSettings;
        _podManager = podManager;
    }

    public ScriptTicket StartScript(StartScriptCommand command)
    {
        var ticketId = Guid.NewGuid().ToString("N");

        if (_scripts.TryGetValue(ticketId, out var existing))
        {
            Log.Information("Reusing in-memory script context for ticket {TicketId}", ticketId);
            return new ScriptTicket(ticketId);
        }

        if (_isolationMutex.TryAcquire(command, out var mutexLock))
        {
            _mutexLocks[ticketId] = mutexLock!;
            LaunchScript(ticketId, command);
        }
        else
        {
            _pendingScripts[ticketId] = command;
            Log.Information("Queued script for ticket {TicketId}, waiting for isolation mutex", ticketId);
        }

        return new ScriptTicket(ticketId);
    }

    private void LaunchScript(string ticketId, StartScriptCommand command)
    {
        var eosMarkerToken = EosMarker.GenerateMarkerToken();
        var wrappedCommand = WrapCommandWithEosMarker(command, eosMarkerToken);

        var workDir = PrepareWorkspace(ticketId, wrappedCommand);
        var podName = _podManager.CreatePod(ticketId);

        _scripts[ticketId] = new ScriptPodContext(ticketId, podName, workDir, eosMarkerToken);

        Log.Information("Started script pod {PodName} for ticket {TicketId}", podName, ticketId);
    }

    private static StartScriptCommand WrapCommandWithEosMarker(StartScriptCommand command, string eosMarkerToken)
    {
        var wrappedBody = EosMarker.WrapScript(command.ScriptBody, eosMarkerToken);

        return new StartScriptCommand(
            wrappedBody,
            command.Isolation,
            command.ScriptIsolationMutexTimeout,
            command.IsolationMutexName,
            command.Arguments,
            command.TaskId,
            command.Files.ToArray());
    }

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        if (_terminalResults.TryGetValue(request.Ticket.TaskId, out var terminal))
            return terminal;

        if (_pendingScripts.ContainsKey(request.Ticket.TaskId))
        {
            var pendingLogs = request.LastLogSequence == 0
                ? new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "Waiting for isolation mutex...") }
                : new List<ProcessOutput>();

            var nextSequence = request.LastLogSequence == 0 ? 1 : request.LastLogSequence;

            return new ScriptStatusResponse(request.Ticket, ProcessState.Running, 0, pendingLogs, nextSequence);
        }

        if (!_scripts.TryGetValue(request.Ticket.TaskId, out var ctx))
            return CompletedResponse(request.Ticket, ScriptExitCodes.UnknownResult);

        var logs = DrainLogs(ctx);

        if (ctx.EosDetected)
            return new ScriptStatusResponse(request.Ticket, ProcessState.Complete, ctx.EosExitCode, logs, ctx.LogSequence);

        var phase = _podManager.GetPodPhase(ctx.PodName);
        var state = MapPhaseToState(phase);
        var exitCode = state == ProcessState.Complete
            ? _podManager.GetPodExitCode(ctx.PodName) : 0;

        return new ScriptStatusResponse(request.Ticket, state, exitCode, logs, ctx.LogSequence);
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        try
        {
            if (_terminalResults.TryRemove(command.Ticket.TaskId, out var terminal))
                return terminal;

            if (!_scripts.TryRemove(command.Ticket.TaskId, out var ctx))
                return CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult);

            _podManager.WaitForPodTermination(ctx.PodName, TimeSpan.FromSeconds(30));

            var logs = DrainFinalLogs(ctx);
            var exitCode = _podManager.GetPodExitCode(ctx.PodName);

            _podManager.DeletePod(ctx.PodName);
            CleanupWorkspace(ctx.WorkDir);

            return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, exitCode, logs, ctx.LogSequence);
        }
        finally
        {
            ReleaseMutexAndProcessPending(command.Ticket.TaskId);
        }
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command)
    {
        if (_pendingScripts.TryRemove(command.Ticket.TaskId, out _))
            return CompletedResponse(command.Ticket, ScriptExitCodes.Canceled);

        try
        {
            if (!_scripts.TryRemove(command.Ticket.TaskId, out var ctx))
                return CompletedResponse(command.Ticket, ScriptExitCodes.Canceled);

            _podManager.DeletePod(ctx.PodName);
            CleanupWorkspace(ctx.WorkDir);

            return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), ctx.LogSequence);
        }
        finally
        {
            ReleaseMutexAndProcessPending(command.Ticket.TaskId);
        }
    }

    public string WorkspaceBasePath => _tentacleSettings.WorkspacePath;

    public ConcurrentDictionary<string, ScriptPodContext> ActiveScripts => _scripts;

    public ConcurrentDictionary<string, StartScriptCommand> PendingScripts => _pendingScripts;

    public void InjectTerminalResult(string ticketId, int exitCode, List<ProcessOutput> logs)
    {
        var response = new ScriptStatusResponse(new ScriptTicket(ticketId), ProcessState.Complete, exitCode, logs, 0);

        _terminalResults[ticketId] = response;
    }

    private void ReleaseMutexAndProcessPending(string ticketId)
    {
        if (_mutexLocks.TryRemove(ticketId, out var lockHandle))
        {
            lockHandle.Dispose();
            ProcessPendingScripts();
        }
    }

    private void ProcessPendingScripts()
    {
        foreach (var kvp in _pendingScripts)
        {
            if (!_isolationMutex.TryAcquire(kvp.Value, out var handle))
                continue;

            if (!_pendingScripts.TryRemove(kvp.Key, out var command))
            {
                handle!.Dispose();
                continue;
            }

            _mutexLocks[kvp.Key] = handle!;

            try
            {
                LaunchScript(kvp.Key, command);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to launch pending script {TicketId}", kvp.Key);
                InjectTerminalResult(kvp.Key, ScriptExitCodes.Fatal, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, ex.Message) });

                if (_mutexLocks.TryRemove(kvp.Key, out var failedLock))
                    failedLock.Dispose();

                continue;
            }

            return;
        }
    }

    private static ScriptStatusResponse CompletedResponse(ScriptTicket ticket, int exitCode)
        => new(ticket, ProcessState.Complete, exitCode, new List<ProcessOutput>(), 0);

    private static ProcessState MapPhaseToState(string? phase)
    {
        return phase switch
        {
            "Succeeded" => ProcessState.Complete,
            "Failed" => ProcessState.Complete,
            KubernetesPodManager.PhaseNotFound => ProcessState.Complete,
            _ => ProcessState.Running
        };
    }
}
