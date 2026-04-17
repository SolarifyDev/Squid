using System.Collections.Concurrent;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Serilog;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Health;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService : IScriptService, ITentacleScriptBackend, IGracefulShutdownAware, IRunningScriptReporter
{
    public bool IsRunningScript(string ticketId)
    {
        if (string.IsNullOrEmpty(ticketId)) return false;
        return _scripts.ContainsKey(ticketId) || _pendingScripts.ContainsKey(ticketId);
    }


    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly KubernetesPodManager _podManager;
    private readonly IKubernetesPodOperations? _podOps;
    private readonly ConcurrentDictionary<string, ScriptPodContext> _scripts = new();
    private readonly ConcurrentDictionary<string, (ScriptStatusResponse Response, DateTimeOffset CreatedAt)> _terminalResults = new();
    private readonly ConcurrentDictionary<string, IDisposable> _mutexLocks = new();
    private readonly ConcurrentDictionary<string, PendingScript> _pendingScripts = new();
    private readonly ScriptIsolationMutex _isolationMutex = new();
    private readonly TimeSpan _mutexTimeout;
    private int _pendingCount;
    private volatile bool _draining;

    public record PendingScript(StartScriptCommand Command, DateTimeOffset EnqueuedAt);

    public ScriptPodService(
        TentacleSettings tentacleSettings,
        KubernetesSettings kubernetesSettings,
        KubernetesPodManager podManager,
        IKubernetesPodOperations? podOps = null)
    {
        _tentacleSettings = tentacleSettings;
        _kubernetesSettings = kubernetesSettings;
        _podManager = podManager;
        _podOps = podOps;
        _mutexTimeout = TimeSpan.FromMinutes(kubernetesSettings.IsolationMutexTimeoutMinutes);
    }

    public ScriptStatusResponse StartScript(StartScriptCommand command)
    {
        if (command.ScriptTicket == null)
            throw new ArgumentException("StartScriptCommand.ScriptTicket is required for idempotent execution", nameof(command));

        var scriptTicket = command.ScriptTicket;
        var ticketId = scriptTicket.TaskId;

        if (_terminalResults.TryGetValue(ticketId, out var terminal))
            return terminal.Response;

        if (_scripts.TryGetValue(ticketId, out var existing))
        {
            Log.Information("Reusing in-memory script context for ticket {TicketId}", ticketId);
            return new ScriptStatusResponse(scriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), existing.LogSequence);
        }

        if (_pendingScripts.ContainsKey(ticketId))
            return new ScriptStatusResponse(scriptTicket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);

        // Crash-safe redelivery: if a prior agent-pod run got as far as writing
        // ScriptStateStore (Starting/Running/Complete) but crashed before the
        // in-memory _scripts entry survived, honour the persisted state rather
        // than re-launching the pod. ScriptRecoveryService populates _scripts
        // for pods that still exist; this catches the case where the workspace
        // PVC retains state but the pod was garbage-collected.
        var persistedWorkDir = Path.Combine(_tentacleSettings.WorkspacePath, ticketId);
        var persisted = TryBuildStatusFromPersistedState(scriptTicket, persistedWorkDir);
        if (persisted != null)
        {
            Log.Information("Redelivered StartScript for ticket {TicketId} — returning persisted state", ticketId);
            return persisted;
        }

        if (_draining)
        {
            InjectTerminalResult(ticketId, ScriptExitCodes.Fatal, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, "Script rejected: Tentacle is shutting down") });
            return _terminalResults[ticketId].Response;
        }

        if (_isolationMutex.TryAcquire(command, out var mutexLock))
        {
            _mutexLocks[ticketId] = mutexLock!;

            try
            {
                LaunchScript(ticketId, command);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to launch script for ticket {TicketId}", ticketId);
                InjectTerminalResult(ticketId, ScriptExitCodes.Fatal, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, ex.Message) });

                if (_mutexLocks.TryRemove(ticketId, out var failedLock))
                    failedLock.Dispose();

                return _terminalResults[ticketId].Response;
            }

            return new ScriptStatusResponse(scriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0);
        }

        var count = Interlocked.Increment(ref _pendingCount);

        if (count > _kubernetesSettings.MaxPendingScripts)
        {
            Interlocked.Decrement(ref _pendingCount);
            Log.Warning("Pending script queue full ({Max}), rejecting ticket {TicketId}", _kubernetesSettings.MaxPendingScripts, ticketId);
            InjectTerminalResult(ticketId, ScriptExitCodes.Fatal, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, $"Script rejected: pending queue full ({_kubernetesSettings.MaxPendingScripts} scripts waiting)") });
            TentacleMetrics.ScriptRejected();
            return _terminalResults[ticketId].Response;
        }

        _pendingScripts[ticketId] = new PendingScript(command, DateTimeOffset.UtcNow);
        PersistPendingSecret(ticketId, command);
        TentacleMetrics.ScriptQueued();
        Log.Information("Queued script for ticket {TicketId}, waiting for isolation mutex", ticketId);

        return new ScriptStatusResponse(scriptTicket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
    }

    private void LaunchScript(string ticketId, StartScriptCommand command)
    {
        DiskSpaceChecker.EnsureDiskHasEnoughFreeSpace(_tentacleSettings.WorkspacePath);

        var eosMarkerToken = EosMarker.GenerateMarkerToken();
        var wrappedCommand = WrapCommandWithEosMarker(command, eosMarkerToken);

        var workDir = PrepareWorkspace(ticketId, wrappedCommand);

        // Persist generic "Starting" state before pod creation so a crash
        // between CreatePod and the in-memory _scripts insert leaves a
        // durable breadcrumb. ScriptRecoveryService reads it on restart;
        // GetStatus falls back to it via TryBuildStatusFromPersistedState.
        PersistStartingState(ticketId, workDir);

        try
        {
            // Script pod always created in agent namespace — PVC, ServiceAccount, and
            // ImagePullSecrets are namespace-scoped and only exist in the agent namespace.
            // TargetNamespace is for kubectl context wrapping (handled by the intent renderer).
            var podName = _podManager.CreatePod(ticketId, additionalLabels: command.Labels);
            RemovePendingSecret(ticketId);

            var ctx = new ScriptPodContext(ticketId, podName, workDir, eosMarkerToken);
            ctx.SensitiveValues = SensitiveVariableDecryptor.ExtractSensitiveValues(workDir);
            _scripts[ticketId] = ctx;
            TentacleMetrics.ScriptStarted();
            StartLogStream(ctx);

            WriteStateFile(workDir, ticketId, podName, eosMarkerToken, command);
            PersistRunningState(ticketId, workDir);

            Log.Information("Started script pod {PodName} for ticket {TicketId}", podName, ticketId);
        }
        catch
        {
            DeletePersistedStateIfAny(workDir);
            CleanupWorkspace(workDir);
            throw;
        }
    }

    private static void WriteStateFile(string workDir, string ticketId, string podName, string eosMarkerToken, StartScriptCommand command)
    {
        try
        {
            ScriptStateFile.Write(workDir, new ScriptStateFile
            {
                TicketId = ticketId,
                PodName = podName,
                EosMarkerToken = eosMarkerToken,
                Isolation = command.Isolation.ToString(),
                IsolationMutexName = command.IsolationMutexName,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write state file for ticket {TicketId}", ticketId);
        }
    }

    private static StartScriptCommand WrapCommandWithEosMarker(StartScriptCommand command, string eosMarkerToken)
    {
        var wrappedBody = EosMarker.WrapScript(command.ScriptBody, eosMarkerToken);

        return new StartScriptCommand(
            command.ScriptTicket,
            wrappedBody,
            command.Isolation,
            command.ScriptIsolationMutexTimeout,
            command.IsolationMutexName,
            command.Arguments,
            command.TaskId,
            command.DurationToWaitForScriptToFinish,
            command.Files.ToArray())
        {
            TargetNamespace = command.TargetNamespace,
            Labels = command.Labels
        };
    }

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        if (_terminalResults.TryGetValue(request.Ticket.TaskId, out var terminal))
            return terminal.Response;

        if (_pendingScripts.TryGetValue(request.Ticket.TaskId, out var pending))
        {
            var age = DateTimeOffset.UtcNow - pending.EnqueuedAt;

            if (age > _mutexTimeout)
            {
                if (_pendingScripts.TryRemove(request.Ticket.TaskId, out _))
                {
                    Interlocked.Decrement(ref _pendingCount);

                    Log.Warning("Pending script {TicketId} timed out after {Minutes:F0}m waiting for isolation mutex",
                        request.Ticket.TaskId, age.TotalMinutes);

                    InjectTerminalResult(request.Ticket.TaskId, ScriptExitCodes.Timeout,
                        new List<ProcessOutput> { new(ProcessOutputSource.StdErr,
                            $"Script timed out waiting for isolation mutex after {age.TotalMinutes:F0} minutes") });

                    return _terminalResults[request.Ticket.TaskId].Response;
                }
            }

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
        var containerTermination = _podManager.GetScriptContainerTermination(ctx.PodName);

        var state = containerTermination != null
            ? ProcessState.Complete
            : MapPhaseToState(phase);

        var exitCode = containerTermination?.ExitCode
            ?? (state == ProcessState.Complete ? _podManager.GetPodExitCode(ctx.PodName) : 0);

        if (containerTermination != null && containerTermination.ExitCode != 0)
        {
            var diagnostic = _podManager.GetContainerDiagnostics(ctx.PodName);

            if (diagnostic != null)
                logs.Insert(0, new ProcessOutput(ProcessOutputSource.StdErr, diagnostic));
        }

        if (state == ProcessState.Running)
        {
            var startupDiag = _podManager.GetPodStartupDiagnostics(ctx.PodName);

            if (startupDiag != null)
            {
                if (startupDiag.IsPermanent)
                {
                    logs.Insert(0, new ProcessOutput(ProcessOutputSource.StdErr, $"Pod startup failed — {startupDiag.Message}"));

                    return new ScriptStatusResponse(request.Ticket, ProcessState.Complete, ScriptExitCodes.PodStartupFailed, logs, ctx.LogSequence);
                }

                logs.Add(new ProcessOutput(ProcessOutputSource.StdErr, $"[K8s Warning] {startupDiag.Message}"));
            }
        }

        if (phase is "Succeeded" or "Failed" && ctx.LogTruncationDetected && !ctx.EosDetected)
        {
            logs.Insert(0, new ProcessOutput(ProcessOutputSource.StdErr,
                "Log rotation detected — script output may be incomplete. Exit code determined from pod phase."));
        }

        return new ScriptStatusResponse(request.Ticket, state, exitCode, logs, ctx.LogSequence);
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        try
        {
            if (_terminalResults.TryRemove(command.Ticket.TaskId, out var terminal))
            {
                DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, command.Ticket.TaskId));
                return terminal.Response;
            }

            if (!_scripts.TryRemove(command.Ticket.TaskId, out var ctx))
            {
                DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, command.Ticket.TaskId));
                return CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult);
            }

            ctx.LogStreamCts?.Cancel();
            _podManager.WaitForPodTermination(ctx.PodName, TimeSpan.FromSeconds(30));

            var logs = DrainFinalLogs(ctx);
            var containerTermination = _podManager.GetScriptContainerTermination(ctx.PodName);
            var exitCode = containerTermination?.ExitCode ?? _podManager.GetPodExitCode(ctx.PodName);

            // Persist Complete state before workspace cleanup so a crash between
            // these two lines still lets a server redeliver and learn the exit code.
            PersistCompleteState(command.Ticket.TaskId, ctx.WorkDir, exitCode, ctx.LogSequence);

            _podManager.DeletePod(ctx.PodName, _kubernetesSettings.ScriptPodGracePeriodSeconds);
            CleanupWorkspace(ctx.WorkDir);

            if (exitCode == 0)
                TentacleMetrics.ScriptCompleted();
            else
                TentacleMetrics.ScriptFailed();

            return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, exitCode, logs, ctx.LogSequence);
        }
        finally
        {
            ReleaseMutexAndProcessPending(command.Ticket.TaskId);
            EvictStaleTerminalResults(TimeSpan.FromHours(1));
        }
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command)
    {
        if (_pendingScripts.TryRemove(command.Ticket.TaskId, out var _))
        {
            Interlocked.Decrement(ref _pendingCount);
            RemovePendingSecret(command.Ticket.TaskId);
            DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, command.Ticket.TaskId));
            TentacleMetrics.ScriptCanceled();
            return CompletedResponse(command.Ticket, ScriptExitCodes.Canceled);
        }

        try
        {
            if (!_scripts.TryRemove(command.Ticket.TaskId, out var ctx))
            {
                DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, command.Ticket.TaskId));
                return CompletedResponse(command.Ticket, ScriptExitCodes.Canceled);
            }

            ctx.LogStreamCts?.Cancel();
            PersistCompleteState(command.Ticket.TaskId, ctx.WorkDir, ScriptExitCodes.Canceled, ctx.LogSequence);
            _podManager.DeletePod(ctx.PodName, _kubernetesSettings.ScriptPodGracePeriodSeconds);
            CleanupWorkspace(ctx.WorkDir);

            TentacleMetrics.ScriptCanceled();

            return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), ctx.LogSequence);
        }
        finally
        {
            ReleaseMutexAndProcessPending(command.Ticket.TaskId);
            EvictStaleTerminalResults(TimeSpan.FromHours(1));
        }
    }

    public string WorkspaceBasePath => _tentacleSettings.WorkspacePath;

    public ConcurrentDictionary<string, ScriptPodContext> ActiveScripts => _scripts;

    public ConcurrentDictionary<string, PendingScript> PendingScripts => _pendingScripts;

    public ConcurrentDictionary<string, IDisposable> MutexLocks => _mutexLocks;

    public ScriptIsolationMutex IsolationMutex => _isolationMutex;

    public void RestoreActiveScript(ScriptPodContext ctx, IDisposable? mutexHandle)
    {
        _scripts[ctx.TicketId] = ctx;

        if (mutexHandle != null)
            _mutexLocks[ctx.TicketId] = mutexHandle;

        Log.Information("Restored active script {TicketId} (pod: {PodName})", ctx.TicketId, ctx.PodName);
    }

    public void InjectTerminalResult(string ticketId, int exitCode, List<ProcessOutput> logs)
    {
        var response = new ScriptStatusResponse(new ScriptTicket(ticketId), ProcessState.Complete, exitCode, logs, 0);

        _terminalResults[ticketId] = (response, DateTimeOffset.UtcNow);
    }

    public void EvictStaleTerminalResults(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;

        foreach (var kvp in _terminalResults)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _terminalResults.TryRemove(kvp.Key, out _);
        }
    }

    public async Task WaitForDrainAsync(TimeSpan timeout)
    {
        _draining = true;

        Log.Information("Draining {Active} active and {Pending} pending scripts (timeout: {Timeout}s)", _scripts.Count, _pendingScripts.Count, timeout.TotalSeconds);

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            while ((_scripts.Count > 0 || _pendingScripts.Count > 0) && !cts.IsCancellationRequested)
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when timeout fires
        }

        if (_scripts.Count > 0)
            Log.Warning("Shutdown drain timed out with {Count} active scripts still running", _scripts.Count);
    }

    public void ReleaseMutexForTicket(string ticketId)
    {
        if (_mutexLocks.TryRemove(ticketId, out var lockHandle))
        {
            lockHandle.Dispose();
            ProcessPendingScripts();
        }
    }

    private void ReleaseMutexAndProcessPending(string ticketId)
    {
        ReleaseMutexForTicket(ticketId);
    }

    private void ProcessPendingScripts(int depth = 0)
    {
        if (_draining) return;
        if (depth > 3) return;

        var launchedWriter = false;
        var retryNeeded = false;

        foreach (var kvp in _pendingScripts.OrderBy(p => p.Value.EnqueuedAt))
        {
            if (launchedWriter) break;

            var age = DateTimeOffset.UtcNow - kvp.Value.EnqueuedAt;

            if (age > _mutexTimeout)
            {
                if (_pendingScripts.TryRemove(kvp.Key, out _))
                {
                    Interlocked.Decrement(ref _pendingCount);

                    Log.Warning("Pending script {TicketId} timed out after {Minutes:F0}m waiting for isolation mutex",
                        kvp.Key, age.TotalMinutes);
                    InjectTerminalResult(kvp.Key, ScriptExitCodes.Timeout,
                        new List<ProcessOutput> { new(ProcessOutputSource.StdErr,
                            $"Script timed out waiting for isolation mutex after {age.TotalMinutes:F0} minutes") });
                }

                continue;
            }

            if (!_isolationMutex.TryAcquire(kvp.Value.Command, out var handle))
                continue;

            if (!_pendingScripts.TryRemove(kvp.Key, out var pending))
            {
                handle!.Dispose();
                retryNeeded = true;
                continue;
            }

            Interlocked.Decrement(ref _pendingCount);

            _mutexLocks[kvp.Key] = handle!;

            try
            {
                LaunchScript(kvp.Key, pending.Command);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to launch pending script {TicketId}", kvp.Key);
                InjectTerminalResult(kvp.Key, ScriptExitCodes.Fatal, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, ex.Message) });

                if (_mutexLocks.TryRemove(kvp.Key, out var failedLock))
                    failedLock.Dispose();

                continue;
            }

            if (pending.Command.Isolation == ScriptIsolationLevel.FullIsolation)
                launchedWriter = true;
        }

        if (retryNeeded && !launchedWriter)
            ProcessPendingScripts(depth + 1);
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

    private void PersistPendingSecret(string ticketId, StartScriptCommand command)
    {
        if (_podOps == null) return;

        try
        {
            var secret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"squid-pending-{ticketId[..12]}",
                    NamespaceProperty = _kubernetesSettings.TentacleNamespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["squid.io/context-type"] = "pending-script",
                        ["squid.io/ticket-id"] = ticketId
                    }
                },
                StringData = new Dictionary<string, string>
                {
                    ["ticketId"] = ticketId,
                    ["scriptBody"] = command.ScriptBody,
                    ["isolation"] = command.Isolation.ToString(),
                    ["isolationMutexName"] = command.IsolationMutexName ?? "",
                    ["targetNamespace"] = command.TargetNamespace ?? "",
                    ["enqueuedAt"] = DateTimeOffset.UtcNow.ToString("O")
                },
                Type = "Opaque"
            };

            _podOps.CreateOrReplaceSecret(secret, _kubernetesSettings.TentacleNamespace);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist pending script Secret for ticket {TicketId}", ticketId);
        }
    }

    private void RemovePendingSecret(string ticketId)
    {
        if (_podOps == null) return;

        try
        {
            _podOps.DeleteSecret($"squid-pending-{ticketId[..12]}", _kubernetesSettings.TentacleNamespace);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to remove pending Secret for ticket {TicketId}", ticketId);
        }
    }
}
