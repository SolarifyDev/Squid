using System.Collections.Concurrent;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Serilog;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Health;
using Squid.Tentacle.ScriptExecution.State;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService : IScriptService, ITentacleScriptBackend, IGracefulShutdownAware, IRunningScriptReporter
{
    public bool IsRunningScript(string ticketId)
    {
        if (string.IsNullOrEmpty(ticketId)) return false;

        // In the direction a ticket moves (pending -> in flight -> tracked): a script whose pod is still being created is running too.
        return IsQueuedOrLaunching(ticketId) || _scripts.ContainsKey(ticketId);
    }


    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly KubernetesPodManager _podManager;
    private readonly IKubernetesPodOperations? _podOps;
    private readonly ConcurrentDictionary<string, ScriptPodContext> _scripts = new();
    private readonly ConcurrentDictionary<string, (ScriptStatusResponse Response, DateTimeOffset CreatedAt)> _terminalResults = new();

    // Tickets whose injected terminal result means "the agent lost track of this script" (V2 reports ResultLost, not a real exit code).
    private readonly ConcurrentDictionary<string, byte> _lostResults = new();

    // Tickets being launched right now (a V1/V2 Start, or a queued script leaving the pending queue), from the claim until the launch ends.
    private readonly ConcurrentDictionary<string, byte> _startsInFlight = new();
    // Cancels that met a ticket mid-launch: the launcher honours them once its pod is tracked, so a Cancel is never lost.
    private readonly ConcurrentDictionary<string, byte> _cancelRequests = new();
    private readonly ConcurrentDictionary<string, IDisposable> _mutexLocks = new();
    private readonly ConcurrentDictionary<string, PendingScript> _pendingScripts = new();
    private readonly ScriptIsolationMutex _isolationMutex = new();
    private readonly TimeSpan _mutexTimeout;
    private int _pendingCount;
    private volatile bool _draining;

    /// <summary>Test-only: runs after a Cancel saw the ticket launching and before it records its cancel request.</summary>
    internal Action? BeforeCancelRequestRecordedHookForTests { get; set; }

    /// <summary>Test-only: runs in Start once the ticket is known to be new, before it is launched or queued.</summary>
    internal Action? StartAdmissionHookForTests { get; set; }

    /// <summary>Test-only: runs in Start after the ticket lock is released, once the start was rejected, queued or claimed for launch.</summary>
    internal Action? AfterStartDecisionHookForTests { get; set; }

    internal bool HasCancelRequestForTests(string ticketId) => _cancelRequests.ContainsKey(ticketId);

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

        return StartOnce(command, () => TryPeekStatusV1(command.ScriptTicket), started => started);
    }

    private enum StartAdmission { Launch, Queued, Rejected }

    /// <summary>
    /// The one start path for V1 and V2. Under the ticket lock a new ticket is either rejected, queued, or claimed for launch (in flight, owning its
    /// isolation mutex) — so a Cancel of it, which claims under the same lock, finds it queued or launching, never in between. The launch itself
    /// (pod creation) runs outside the lock, see <see cref="LaunchClaimed"/>.
    /// </summary>
    private T StartOnce<T>(StartScriptCommand command, Func<T?> peekKnown, Func<ScriptStatusResponse, T> map) where T : class
    {
        StartAdmission admission;
        ScriptStatusResponse? rejection;

        lock (_ticketLocks.For(command.ScriptTicket.TaskId))
        {
            var known = peekKnown();

            if (known != null) return known;

            StartAdmissionHookForTests?.Invoke();

            admission = Admit(command, out rejection);
        }

        AfterStartDecisionHookForTests?.Invoke();

        return map(admission switch
        {
            StartAdmission.Launch => LaunchFromStart(command),
            StartAdmission.Queued => AnnounceQueued(command),
            _ => rejection!
        });
    }

    // Under the ticket lock. In memory (terminal, tracked, queued or launching) answers from memory; otherwise a pod a previous agent run
    // launched and persisted (Starting excluded) answers from disk instead of being launched again.
    private ScriptStatusResponse? TryPeekStatusV1(ScriptTicket ticket)
    {
        var ticketId = ticket.TaskId;

        if (_terminalResults.TryGetValue(ticketId, out var terminal)) return terminal.Response;

        if (_scripts.TryGetValue(ticketId, out var existing))
        {
            Log.Information("Reusing in-memory script context for ticket {TicketId}", ticketId);
            return new ScriptStatusResponse(ticket, ProcessState.Running, 0, new List<ProcessOutput>(), existing.LogSequence);
        }

        if (IsQueuedOrLaunching(ticketId)) return new ScriptStatusResponse(ticket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);

        var persisted = TryBuildStatusFromPersistedState(ticket, Path.Combine(_tentacleSettings.WorkspacePath, ticketId));

        if (persisted != null) Log.Information("Redelivered StartScript for ticket {TicketId} — returning persisted state", ticketId);

        return persisted;
    }

    // Under the ticket lock: no I/O beyond the in-memory queue and mutex.
    private StartAdmission Admit(StartScriptCommand command, out ScriptStatusResponse? rejection)
    {
        var ticketId = command.ScriptTicket.TaskId;

        rejection = null;

        if (_draining)
        {
            rejection = Reject(ticketId, "Script rejected: Tentacle is shutting down");
            return StartAdmission.Rejected;
        }

        if (_isolationMutex.TryAcquire(command, out var mutexLock))
        {
            ClaimForLaunch(ticketId, mutexLock!);
            return StartAdmission.Launch;
        }

        if (TryEnqueue(command)) return StartAdmission.Queued;

        rejection = Reject(ticketId, $"Script rejected: pending queue full ({_kubernetesSettings.MaxPendingScripts} scripts waiting)");
        TentacleMetrics.ScriptRejected();

        return StartAdmission.Rejected;
    }

    private bool TryEnqueue(StartScriptCommand command)
    {
        if (Interlocked.Increment(ref _pendingCount) > _kubernetesSettings.MaxPendingScripts)
        {
            Interlocked.Decrement(ref _pendingCount);
            Log.Warning("Pending script queue full ({Max}), rejecting ticket {TicketId}", _kubernetesSettings.MaxPendingScripts, command.ScriptTicket.TaskId);
            return false;
        }

        _pendingScripts[command.ScriptTicket.TaskId] = new PendingScript(command, DateTimeOffset.UtcNow);

        return true;
    }

    private ScriptStatusResponse Reject(string ticketId, string message)
    {
        InjectTerminalResult(ticketId, ScriptExitCodes.Fatal, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, message) });

        return _terminalResults[ticketId].Response;
    }

    private ScriptStatusResponse AnnounceQueued(StartScriptCommand command)
    {
        PersistPendingSecret(command.ScriptTicket.TaskId, command);
        RemovePendingSecretUnlessQueued(command.ScriptTicket.TaskId);
        TentacleMetrics.ScriptQueued();
        Log.Information("Queued script for ticket {TicketId}, waiting for isolation mutex", command.ScriptTicket.TaskId);

        return new ScriptStatusResponse(command.ScriptTicket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
    }

    // The Secret is written after the ticket lock is released, so the ticket may have left the queue first and whoever took it out found no
    // Secret to delete. Every exit from the queue removes the ticket before it deletes the Secret: a ticket still queued here is deleted later.
    private void RemovePendingSecretUnlessQueued(string ticketId)
    {
        if (!_pendingScripts.ContainsKey(ticketId)) RemovePendingSecret(ticketId);
    }

    // The ticket is in flight and owns the isolation mutex from here until LaunchClaimed ends the launch.
    private void ClaimForLaunch(string ticketId, IDisposable mutexLock)
    {
        _mutexLocks[ticketId] = mutexLock;
        _startsInFlight[ticketId] = 0;
    }

    // A launch that did not leave a running pod (failed, or cancelled as it launched) freed the mutex: a queued script may now start.
    private ScriptStatusResponse LaunchFromStart(StartScriptCommand command)
    {
        var launched = LaunchClaimed(command);

        if (launched.State != ProcessState.Running) ProcessPendingScripts();

        return launched;
    }

    /// <summary>
    /// The one launch path (V1 Start, V2 Start, a script leaving the pending queue). On entry the ticket is in flight and owns its isolation mutex;
    /// on exit it is no longer in flight and is either running, cancelled (a Cancel that met the launch, its mutex released) or failed (Fatal result,
    /// its mutex released). Only this launcher releases the mutex while the ticket is in flight: a Cancel meeting it hands the cancel over instead.
    /// </summary>
    private ScriptStatusResponse LaunchClaimed(StartScriptCommand command)
    {
        var ticketId = command.ScriptTicket.TaskId;

        try
        {
            if (!LaunchScript(ticketId, command)) return CancelledLaunchResult(command.ScriptTicket);

            return new ScriptStatusResponse(command.ScriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0);
        }
        catch (Exception ex)
        {
            return FailLaunch(ticketId, ex);
        }
        finally
        {
            _startsInFlight.TryRemove(ticketId, out _);
            DropUnhonouredCancelRequest(ticketId);
        }
    }

    // The launcher published the Canceled result when it cancelled its pod; a Complete may have consumed it since.
    private ScriptStatusResponse CancelledLaunchResult(ScriptTicket ticket)
        => _terminalResults.TryGetValue(ticket.TaskId, out var cancelled) ? cancelled.Response : CompletedResponse(ticket, ScriptExitCodes.Canceled);

    // The Fatal result is published while the ticket is still in flight, so a lock-free reader never finds it in neither place.
    private ScriptStatusResponse FailLaunch(string ticketId, Exception ex)
    {
        Log.Error(ex, "Failed to launch script for ticket {TicketId}", ticketId);

        var failed = Reject(ticketId, ex.Message);

        ReleaseLaunchMutex(ticketId);

        return failed;
    }

    private void ReleaseLaunchMutex(string ticketId)
    {
        if (_mutexLocks.TryRemove(ticketId, out var lockHandle))
            lockHandle.Dispose();
    }

    /// <summary>Returns false when a Cancel that met the launch was honoured: the pod is gone, the Canceled result is injected and the isolation mutex released.</summary>
    private bool LaunchScript(string ticketId, StartScriptCommand command)
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

            var cancelled = HonourCancelRequestedDuringLaunch(ticketId);

            AfterHonourCheckHookForTests?.Invoke();

            return !cancelled;
        }
        catch
        {
            RemovePendingSecret(ticketId);
            DeletePersistedStateIfAny(workDir);
            CleanupWorkspace(workDir);
            throw;
        }
    }

    // Checked AFTER the pod is tracked: a Cancel records its request before it re-reads the tracked set, so either it sees the pod
    // and cancels it itself, or this check sees the request. Whoever removes the request owns the cancellation.
    private bool HonourCancelRequestedDuringLaunch(string ticketId)
    {
        if (!_cancelRequests.TryRemove(ticketId, out _)) return false;

        Log.Information("Cancel requested while ticket {TicketId} was launching, cancelling its pod", ticketId);

        CancelScriptCore(new CancelScriptCommand(new ScriptTicket(ticketId), 0), deferredToCaller: true);

        ReleaseLaunchMutex(ticketId);

        return true;
    }

    /// <summary>Test-only: runs in the launcher after it checked for a cancel request, with the pod tracked, before the launch is reported finished.</summary>
    internal Action? AfterHonourCheckHookForTests { get; set; }

    /// <summary>Test-only: runs after a Cancel recorded its request and before it arbitrates ownership of it.</summary>
    internal Action? AfterCancelRequestRecordedHookForTests { get; set; }

    // Called once the ticket is no longer in-flight. A request is only ever discarded when no pod was launched (a failed launch): with a pod
    // tracked, the request is either already consumed by the launcher's check or belongs to the Cancel that recorded it, which cancels the pod itself.
    private void DropUnhonouredCancelRequest(string ticketId)
    {
        if (!_scripts.ContainsKey(ticketId)) _cancelRequests.TryRemove(ticketId, out _);
    }

    /// <summary>
    /// A ticket mid-launch has no pod to cancel yet: record the request for the launcher. Returns true when the launcher took it over; false when the
    /// pod is already tracked (the caller cancels it) or the launch already ended (nothing left to cancel).
    /// </summary>
    private bool TryHandCancelToLauncher(string ticketId)
    {
        if (!_startsInFlight.ContainsKey(ticketId)) return false;

        BeforeCancelRequestRecordedHookForTests?.Invoke();

        _cancelRequests[ticketId] = 0;

        AfterCancelRequestRecordedHookForTests?.Invoke();

        if (_scripts.ContainsKey(ticketId)) return !_cancelRequests.TryRemove(ticketId, out _);

        if (_startsInFlight.ContainsKey(ticketId)) return true;

        _cancelRequests.TryRemove(ticketId, out _);

        return false;
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
        var ticketId = request.Ticket.TaskId;

        // Read in the direction a ticket moves (pending -> in flight -> tracked -> terminal): a transition publishes the next set before it leaves
        // the previous one, so a read in this order cannot miss a ticket that moves while we look. Terminal then wins over the stale entry.
        _pendingScripts.TryGetValue(ticketId, out var pending);
        var launching = IsQueuedOrLaunching(ticketId);
        var tracked = _scripts.TryGetValue(ticketId, out var ctx);

        StatusSnapshotHookForTests?.Invoke();

        if (_terminalResults.TryGetValue(ticketId, out var terminal))
            return terminal.Response;

        if (pending != null) return BuildPendingStatus(request, pending);

        if (!tracked) return launching ? LaunchingStatus(request.Ticket, request.LastLogSequence) : CompletedResponse(request.Ticket, ScriptExitCodes.UnknownResult);

        var status = BuildActiveStatus(request, ctx!);

        // Isolation protects the running pod only: once the pod is done the lock is free even if CompleteScript never
        // arrives. Idempotent (TryRemove), so the later Complete/Cancel release is a no-op.
        if (status.State == ProcessState.Complete)
            ReleaseMutexForTicket(ticketId);

        return status;
    }

    // A script whose pod is still being created is running: answering Complete would make the server complete (and forget) it.
    private static ScriptStatusResponse LaunchingStatus(ScriptTicket ticket, long lastLogSequence)
        => new(ticket, ProcessState.Running, 0, new List<ProcessOutput>(), lastLogSequence);

    private ScriptStatusResponse BuildPendingStatus(ScriptStatusRequest request, PendingScript pending)
    {
        var age = DateTimeOffset.UtcNow - pending.EnqueuedAt;

        if (age > _mutexTimeout && TryExpirePending(request.Ticket.TaskId, pending, age))
            return _terminalResults[request.Ticket.TaskId].Response;

        var pendingLogs = request.LastLogSequence == 0
            ? new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "Waiting for isolation mutex...") }
            : new List<ProcessOutput>();

        var nextSequence = request.LastLogSequence == 0 ? 1 : request.LastLogSequence;

        return new ScriptStatusResponse(request.Ticket, ProcessState.Running, 0, pendingLogs, nextSequence);
    }

    /// <summary>Test-only: runs in V1 GetStatus after the pending and tracked reads and before the terminal read.</summary>
    internal Action? StatusSnapshotHookForTests { get; set; }

    // A queued script that waited past the isolation-mutex timeout gets its Timeout result published before it leaves the queue.
    private bool TryExpirePending(string ticketId, PendingScript pending, TimeSpan age)
    {
        var message = $"Script timed out waiting for isolation mutex after {age.TotalMinutes:F0} minutes";
        var response = BuildTerminalResponse(ticketId, ScriptExitCodes.Timeout, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, message) });

        if (!PublishTerminalThenForget(_pendingScripts, ticketId, pending, response)) return false;

        Interlocked.Decrement(ref _pendingCount);
        RemovePendingSecret(ticketId);

        Log.Warning("Pending script {TicketId} timed out after {Minutes:F0}m waiting for isolation mutex", ticketId, age.TotalMinutes);

        return true;
    }

    /// <summary>
    /// Moves a ticket from <paramref name="source"/> to the terminal results WITHOUT a moment where it is in neither: the terminal result is published first and the
    /// source entry removed second, so a lock-free reader always finds the ticket somewhere (a miss reads as lost/unknown and the server fails or re-dispatches the step).
    /// Serialized through the ticket lock with every other step that takes the ticket out of pending or tracked (Start, the dequeue, Cancel, Complete);
    /// false (and nothing published) when the entry is no longer the one passed in.
    /// </summary>
    private bool PublishTerminalThenForget<TEntry>(ConcurrentDictionary<string, TEntry> source, string ticketId, TEntry entry, ScriptStatusResponse response)
    {
        lock (_ticketLocks.For(ticketId))
        {
            var published = (response, DateTimeOffset.UtcNow);

            if (!source.TryGetValue(ticketId, out var current) || !EqualityComparer<TEntry>.Default.Equals(current, entry)) return false;

            if (!_terminalResults.TryAdd(ticketId, published)) return false;

            BetweenPublishAndForgetHookForTests?.Invoke();

            // Every step that takes a ticket out of pending or tracked holds this lock, so the entry checked above is still there: a published
            // result is never retracted (a reader may already have answered with it).
            source.TryRemove(ticketId, out _);

            return true;
        }
    }

    /// <summary>Test-only: runs after a terminal result is published and before the ticket leaves its previous set.</summary>
    internal Action? BetweenPublishAndForgetHookForTests { get; set; }

    /// <summary>Publishes the terminal result of a pod the monitor gave up on (stuck in Pending), then stops tracking it. False when the ticket was finished by someone else first.</summary>
    internal bool TryFailTracked(string ticketId, ScriptPodContext ctx, int exitCode, List<ProcessOutput> logs)
        => PublishTerminalThenForget(_scripts, ticketId, ctx, BuildTerminalResponse(ticketId, exitCode, logs));

    private static ScriptStatusResponse BuildTerminalResponse(string ticketId, int exitCode, List<ProcessOutput> logs)
        => new(new ScriptTicket(ticketId), ProcessState.Complete, exitCode, logs, 0);

    private ScriptStatusResponse BuildActiveStatus(ScriptStatusRequest request, ScriptPodContext ctx)
    {
        // Read the pod's state BEFORE draining its logs: a pod that finished between the two reads would otherwise be reported
        // Complete with logs read while it was still running (missing its final lines), and a Complete poll releases the isolation mutex.
        var phase = _podManager.GetPodPhase(ctx.PodName);
        var containerTermination = _podManager.GetScriptContainerTermination(ctx.PodName);

        var logs = DrainLogs(ctx);

        if (ctx.EosDetected)
            return new ScriptStatusResponse(request.Ticket, ProcessState.Complete, ctx.EosExitCode, logs, ctx.LogSequence);

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

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command) => CompleteScriptCore(command, null, deferredToCaller: false, out _);

    /// <summary>
    /// <paramref name="recordFinal"/> (V2) records the final result somewhere durable BEFORE the persisted state is released; it
    /// returns false when that failed, in which case the state file stays so a crash does not turn a finished script into an unknown one.
    /// <paramref name="deferredToCaller"/> (V2, which holds a shared ticket lock here) means the caller has already stopped the log stream and
    /// awaited the pod's termination, and will release the isolation mutex (which launches the next queued script) after dropping its lock.
    /// <paramref name="ownsMutex"/>: this Complete took the tracked pod, so the ticket's isolation mutex is its to release, see <see cref="TakeForComplete"/>.
    /// </summary>
    internal ScriptStatusResponse CompleteScriptCore(CompleteScriptCommand command, Func<ScriptStatusResponse, bool>? recordFinal, bool deferredToCaller, out bool ownsMutex)
    {
        ScriptPodContext? ctx = null;

        try
        {
            ctx = TakeForComplete(command.Ticket.TaskId, out var terminal, out var launching);

            AfterTicketClaimedHookForTests?.Invoke();

            if (terminal != null) return CompleteTerminal(command.Ticket, terminal, recordFinal);

            if (ctx != null) return CompleteTrackedPod(command.Ticket, ctx, recordFinal, deferredToCaller);

            // A queued or launching script has no result yet, and its launch (not this Complete) owns its state and isolation mutex.
            // The answer stays Complete + UnknownResult as before: a V1 server reads any Complete answer's exit code as the result,
            // so a Running answer here (exit code 0) could be taken for success.
            if (launching) return CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult);

            DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, command.Ticket.TaskId));

            return CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult);
        }
        finally
        {
            ownsMutex = ctx != null;

            if (!deferredToCaller && ownsMutex) ReleaseMutexAndProcessPending(command.Ticket.TaskId);

            EvictStaleTerminalResults(TimeSpan.FromHours(1));
        }
    }

    private ScriptStatusResponse CompleteTerminal(ScriptTicket ticket, ScriptStatusResponse terminal, Func<ScriptStatusResponse, bool>? recordFinal)
    {
        _lostResults.TryRemove(ticket.TaskId, out _);

        if (recordFinal?.Invoke(terminal) ?? true)
            DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, ticket.TaskId));

        return terminal;
    }

    private ScriptStatusResponse CompleteTrackedPod(ScriptTicket ticket, ScriptPodContext ctx, Func<ScriptStatusResponse, bool>? recordFinal, bool deferredToCaller)
    {
        if (!deferredToCaller)
        {
            StopLogStream(ctx);
            _podManager.WaitForPodTermination(ctx.PodName, PodTerminationWait);
        }

        var logs = DrainFinalLogs(ctx);
        var containerTermination = _podManager.GetScriptContainerTermination(ctx.PodName);
        var exitCode = containerTermination?.ExitCode ?? _podManager.GetPodExitCode(ctx.PodName);

        // Persist Complete state before workspace cleanup so a crash between
        // these two lines still lets a server redeliver and learn the exit code.
        PersistCompleteState(ticket.TaskId, ctx.WorkDir, exitCode, ctx.LogSequence);

        var final = new ScriptStatusResponse(ticket, ProcessState.Complete, exitCode, logs, ctx.LogSequence);

        _podManager.DeletePod(ctx.PodName, _kubernetesSettings.ScriptPodGracePeriodSeconds);

        if (recordFinal?.Invoke(final) ?? true)
            CleanupWorkspace(ctx.WorkDir);
        else
            StateOnlyWorkspace.Scrub(ctx.WorkDir);

        if (exitCode == 0)
            TentacleMetrics.ScriptCompleted();
        else
            TentacleMetrics.ScriptFailed();

        return final;
    }

    private static readonly TimeSpan PodTerminationWait = TimeSpan.FromSeconds(30);

    /// <summary>Test-only: runs in Complete and Cancel once the ticket lock is released after they claimed (or found nothing to claim).</summary>
    internal Action? AfterTicketClaimedHookForTests { get; set; }

    /// <summary>Test-only: runs in Complete after it looked for a terminal result and before it takes the tracked pod.</summary>
    internal Action? CompleteTakeHookForTests { get; set; }

    /// <summary>
    /// Under the ticket lock, like every other step that takes a ticket out of tracked or terminal: the terminal result if any, else the tracked pod.
    /// The isolation mutex goes with the tracked pod: only the Complete or Cancel that takes it out of tracked releases it, never one that took a
    /// result or nothing (a launch, or whoever published the result, owns the mutex then). <paramref name="launching"/> is read first, see <see cref="IsQueuedOrLaunching"/>.
    /// </summary>
    private ScriptPodContext? TakeForComplete(string ticketId, out ScriptStatusResponse? terminal, out bool launching)
    {
        lock (_ticketLocks.For(ticketId))
        {
            launching = IsQueuedOrLaunching(ticketId);
            terminal = _terminalResults.TryRemove(ticketId, out var published) ? published.Response : null;

            CompleteTakeHookForTests?.Invoke();

            return terminal == null && _scripts.TryRemove(ticketId, out var ctx) ? ctx : null;
        }
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command) => CancelScriptCore(command, deferredToCaller: false);

    /// <param name="deferredToCaller">V2 holds a shared ticket lock here: the caller releases the isolation mutex (launching the next queued script) after dropping it.</param>
    internal ScriptStatusResponse CancelScriptCore(CancelScriptCommand command, bool deferredToCaller)
        => CancelScriptCore(command, deferredToCaller, out _);

    /// <summary>Test-only: runs at the start of <see cref="CancelScriptCore(CancelScriptCommand, bool, out CancelClaim)"/>, before the pending queue is touched.</summary>
    internal Action? BeforeCancelCoreHookForTests { get; set; }

    internal ScriptStatusResponse CancelScriptCore(CancelScriptCommand command, bool deferredToCaller, out CancelClaim claim)
    {
        BeforeCancelCoreHookForTests?.Invoke();

        claim = ClaimForCancel(command.Ticket, deferredToCaller, out var ctx);

        AfterTicketClaimedHookForTests?.Invoke();

        try
        {
            return claim switch
            {
                CancelClaim.Queued => CancelQueued(command.Ticket),
                CancelClaim.Tracked => CancelTrackedPod(command.Ticket, ctx!),
                CancelClaim.HandedToLauncher => CompletedResponse(command.Ticket, ScriptExitCodes.Canceled),
                _ => CancelUnknown(command.Ticket)
            };
        }
        finally
        {
            // The mutex goes with the tracked pod (see TakeForComplete): a queued script holds none, a launching one's belongs to its launcher.
            if (!deferredToCaller && claim == CancelClaim.Tracked) ReleaseMutexAndProcessPending(command.Ticket.TaskId);

            EvictStaleTerminalResults(TimeSpan.FromHours(1));
        }
    }

    internal enum CancelClaim { Queued, HandedToLauncher, Tracked, None }

    /// <summary>
    /// Under the ticket lock, serialized with Start's launch-or-queue decision, the dequeue and every publish-then-forget step of this ticket: a queued
    /// script is taken from the queue, a launching one is handed to its launcher (or, once its pod is tracked, cancelled here), a tracked one is taken.
    /// </summary>
    private CancelClaim ClaimForCancel(ScriptTicket ticket, bool publishCanceled, out ScriptPodContext? ctx)
    {
        ctx = null;

        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (TryTakePendingForCancel(ticket, publishCanceled)) return CancelClaim.Queued;

            if (TryTakeTrackedForCancel(ticket, publishCanceled, out ctx)) return CancelClaim.Tracked;

            // Not tracked yet: either the launcher takes the cancel over, or it tracked the pod while the request was being recorded and we take that pod.
            if (TryHandCancelToLauncher(ticket.TaskId)) return CancelClaim.HandedToLauncher;

            return TryTakeTrackedForCancel(ticket, publishCanceled, out ctx) ? CancelClaim.Tracked : CancelClaim.None;
        }
    }

    private ScriptStatusResponse CancelQueued(ScriptTicket ticket)
    {
        Interlocked.Decrement(ref _pendingCount);
        RemovePendingSecret(ticket.TaskId);
        DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, ticket.TaskId));
        TentacleMetrics.ScriptCanceled();

        return CompletedResponse(ticket, ScriptExitCodes.Canceled);
    }

    private ScriptStatusResponse CancelTrackedPod(ScriptTicket ticket, ScriptPodContext ctx)
    {
        StopLogStream(ctx);
        PersistCompleteState(ticket.TaskId, ctx.WorkDir, ScriptExitCodes.Canceled, ctx.LogSequence);
        _podManager.DeletePod(ctx.PodName, _kubernetesSettings.ScriptPodGracePeriodSeconds);
        CleanupWorkspace(ctx.WorkDir);

        TentacleMetrics.ScriptCanceled();

        return new ScriptStatusResponse(ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), ctx.LogSequence);
    }

    private ScriptStatusResponse CancelUnknown(ScriptTicket ticket)
    {
        DeletePersistedStateIfAny(Path.Combine(_tentacleSettings.WorkspacePath, ticket.TaskId));

        return CompletedResponse(ticket, ScriptExitCodes.Canceled);
    }

    // publishCanceled (V2 and the launcher): the Canceled result is the ticket's terminal result and is published BEFORE the ticket stops being
    // tracked, so a lock-free status read never sees it in neither place. V1 reports the cancel in the response only and keeps no terminal result.
    private bool TryTakePendingForCancel(ScriptTicket ticket, bool publishCanceled)
    {
        if (!publishCanceled) return _pendingScripts.TryRemove(ticket.TaskId, out _);

        return _pendingScripts.TryGetValue(ticket.TaskId, out var pending) && PublishTerminalThenForget(_pendingScripts, ticket.TaskId, pending, CompletedResponse(ticket, ScriptExitCodes.Canceled));
    }

    private bool TryTakeTrackedForCancel(ScriptTicket ticket, bool publishCanceled, out ScriptPodContext? ctx)
    {
        if (!publishCanceled) return _scripts.TryRemove(ticket.TaskId, out ctx);

        return _scripts.TryGetValue(ticket.TaskId, out ctx) && PublishTerminalThenForget(_scripts, ticket.TaskId, ctx, CompletedResponse(ticket, ScriptExitCodes.Canceled));
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
        _terminalResults[ticketId] = (BuildTerminalResponse(ticketId, exitCode, logs), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Injects a terminal result for a script the agent can no longer account for (restart recovery). V1 sees the usual
    /// UnknownResult code; V2 reports ResultLost for it, for as long as the entry lives and after it is evicted.
    /// </summary>
    public void InjectLostResult(string ticketId, List<ProcessOutput> logs)
    {
        _lostResults[ticketId] = 0;

        InjectTerminalResult(ticketId, ScriptExitCodes.UnknownResult, logs);
    }

    public void EvictStaleTerminalResults(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;

        foreach (var kvp in _terminalResults)
        {
            if (kvp.Value.CreatedAt < cutoff && _terminalResults.TryRemove(kvp.Key, out _))
                _lostResults.TryRemove(kvp.Key, out _);
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

    /// <summary>Test-only: runs after a queued script is marked in-flight and before it is removed from the pending queue.</summary>
    internal Action? BetweenStartMarkAndDequeueHookForTests { get; set; }

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
                TryExpirePending(kvp.Key, kvp.Value, age);

                continue;
            }

            if (!_isolationMutex.TryAcquire(kvp.Value.Command, out var handle))
                continue;

            if (!TryClaimPendingForLaunch(kvp.Key, kvp.Value, handle!))
            {
                handle!.Dispose();
                retryNeeded = true;
                continue;
            }

            if (LaunchClaimed(kvp.Value.Command).State != ProcessState.Running) continue;

            if (kvp.Value.Command.Isolation == ScriptIsolationLevel.FullIsolation)
                launchedWriter = true;
        }

        if (retryNeeded && !launchedWriter)
            ProcessPendingScripts(depth + 1);
    }

    /// <summary>
    /// Under the ticket lock, so a Cancel or a timeout of this queued script either took it from the queue first (false) or finds it launching.
    /// Marked in flight BEFORE it leaves the queue: a lock-free reader scanning pending, then in flight, never finds it in neither.
    /// </summary>
    private bool TryClaimPendingForLaunch(string ticketId, PendingScript pending, IDisposable mutexLock)
    {
        lock (_ticketLocks.For(ticketId))
        {
            if (!_pendingScripts.TryGetValue(ticketId, out var current) || !ReferenceEquals(current, pending)) return false;

            ClaimForLaunch(ticketId, mutexLock);

            BetweenStartMarkAndDequeueHookForTests?.Invoke();

            _pendingScripts.TryRemove(ticketId, out _);
        }

        Interlocked.Decrement(ref _pendingCount);

        return true;
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
