using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Serilog;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Observability;
using Squid.Tentacle.Platform;
using Squid.Tentacle.ScriptExecution.Logging;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;
using Squid.Tentacle.Security.Admission;

namespace Squid.Tentacle.ScriptExecution;

public partial class LocalScriptService : IScriptService, IScriptServiceV2, ITentacleScriptBackend, IGracefulShutdownAware, IRunningScriptReporter
{
    // In the direction a ticket moves (in flight -> tracked): a script being started is running too, its workspace is being written.
    public bool IsRunningScript(string ticketId)
        => !string.IsNullOrEmpty(ticketId) && (_startsInFlight.ContainsKey(ticketId) || _scripts.ContainsKey(ticketId));


    private readonly ConcurrentDictionary<string, RunningScript> _scripts = new();
    private readonly ScriptIsolationMutex _isolationMutex = new();
    /// <summary>
    ///  (audit ARCH.9 Plan A) — per-ticket soft-cancellation registry.
    /// Bridges the wire-level "no CT" limitation with internal async work
    /// (file save, mutex acquire) so a CancelScript RPC can actually
    /// short-circuit in-flight operations rather than letting them run to
    /// completion. <see cref="StartScript"/> calls
    /// <see cref="ScriptCancellationRegistry.GetOrCreate"/>;
    /// <see cref="CancelScript"/> calls <see cref="ScriptCancellationRegistry.Cancel"/>;
    /// <see cref="CompleteScript"/> calls <see cref="ScriptCancellationRegistry.Cleanup"/>.
    /// </summary>
    private readonly ScriptCancellationRegistry _cancellationRegistry = new();
    private readonly ScriptTombstoneStore _tombstones;
    private readonly TicketLockStripes _ticketLocks = new();
    // Tickets being started right now (V1 or V2 Start), from the claim under the ticket lock until the script is tracked or the launch failed.
    private readonly ConcurrentDictionary<string, byte> _startsInFlight = new();
    // Cancels that met a ticket being started: its starter carries them out once the script is tracked, so a Cancel is never lost.
    private readonly ConcurrentDictionary<string, CancelOrigin> _cancelRequests = new();

    // Which protocol's cancel the starter carries out: V1 releases the ticket, V2 keeps its Canceled result for the V2 Complete.
    private enum CancelOrigin { V1, V2 }

    /// <summary>Test-only — observe the per-ticket CTS registry's live entry count.
    /// Used to verify Cleanup is called on every exit path of LocalScriptService.</summary>
    internal int CancellationRegistryCountForTests => _cancellationRegistry.CountForTests;

    /// <summary>Test-only — runs after a status poll has read the log file and before it decides the script's state.</summary>
    internal Action? StatusLogsReadHookForTests { get; set; }

    /// <summary>Test-only — runs inside V2 CompleteScript between reading the start-in-flight flag and looking the script up.</summary>
    internal Action? CompleteStateLookupHookForTests { get; set; }

    /// <summary>Test-only — runs right after the pre-lock output drain and before the ticket lock is taken (the window in which a still-running script can exit).</summary>
    internal Action? AfterPreLockDrainHookForTests { get; set; }

    /// <summary>Test-only — runs in a start once it holds its isolation mutex, before its workspace is created.</summary>
    internal Action? StartUnderwayHookForTests { get; set; }

    /// <summary>Test-only — runs in a start after its files are written, before its process is started.</summary>
    internal Action? AfterFilesWrittenHookForTests { get; set; }

    /// <summary>Test-only — runs in a Cancel that saw the ticket being started, before it records its cancel request.</summary>
    internal Action? BeforeCancelRequestRecordedHookForTests { get; set; }

    private readonly IScriptStateStoreFactory _stateStoreFactory;
    private readonly IAdmissionPolicySource? _admissionPolicySource;
    private volatile bool _draining;
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// env-var override for the orphan-workspace TTL.
    /// Default 24h was hardcoded ; operators with high deploy
    /// throughput (100 scripts/h × 100 MB workdir = ~240 GB/day stale
    /// accumulation) need to tighten this aggressively. Operators with rare
    /// deploys may want to LOOSEN it (so a workspace stays around long enough
    /// for post-mortem inspection).
    ///
    /// <para>Value is hours (integer). Out-of-range / unparseable input falls
    /// back to <see cref="DefaultOrphanMaxAgeHours"/> with a Serilog warning.
    /// Pinned literal: <c>SQUID_TENTACLE_ORPHAN_WORKSPACE_TTL_HOURS</c>.</para>
    /// </summary>
    public const string OrphanMaxAgeEnvVar = "SQUID_TENTACLE_ORPHAN_WORKSPACE_TTL_HOURS";

    public const int DefaultOrphanMaxAgeHours = 24;
    public const int MinOrphanMaxAgeHours = 1;
    public const int MaxOrphanMaxAgeHours = 24 * 30;  // 30 days

    /// <summary>
    /// Read once at first access, cached for process lifetime. Reading at every
    /// cleanup tick would let an operator unset / re-set the env var mid-run,
    /// but that's not a documented use case and would be confusing.
    /// </summary>
    internal static readonly TimeSpan OrphanMaxAge = ResolveOrphanMaxAge();

    private static TimeSpan ResolveOrphanMaxAge()
    {
        var raw = Environment.GetEnvironmentVariable(OrphanMaxAgeEnvVar);

        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromHours(DefaultOrphanMaxAgeHours);

        if (!int.TryParse(raw, out var hours) || hours < MinOrphanMaxAgeHours || hours > MaxOrphanMaxAgeHours)
        {
            Log.Warning(
                "{EnvVar}='{RawValue}' is not a valid integer in [{Min}..{Max}] hours; " +
                "falling back to default {Default}h. Set to a positive integer to override.",
                OrphanMaxAgeEnvVar, raw, MinOrphanMaxAgeHours, MaxOrphanMaxAgeHours, DefaultOrphanMaxAgeHours);
            return TimeSpan.FromHours(DefaultOrphanMaxAgeHours);
        }

        Log.Information(
            "Orphan workspace TTL configured to {Hours} hours via {EnvVar}",
            hours, OrphanMaxAgeEnvVar);

        return TimeSpan.FromHours(hours);
    }
    // Canonicalised version (strips trailing .0 Revision) — keeps deployment
    // audit logs aligned with /upgrade-info.currentVersion and the upgrade
    // target version format.
    private static readonly string AgentVersion = Core.AssemblyVersion.Canonical;

    /// <summary>
    /// How long after a script's process exits we keep waiting for its stdout/stderr to reach EOF before we stop
    /// reading. A script that leaves a background child holding the pipe open would otherwise block every waiter
    /// until that child exits.
    /// </summary>
    internal static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(10);

    internal TimeSpan OutputDrainTimeout { get; init; } = DefaultOutputDrainTimeout;

    public LocalScriptService() : this(new ScriptStateStoreFactory()) { }

    public LocalScriptService(IScriptStateStoreFactory stateStoreFactory, IAdmissionPolicySource? admissionPolicySource = null)
        : this(stateStoreFactory, admissionPolicySource, ScriptTombstoneStore.ForTempRoot())
    {
    }

    internal LocalScriptService(IScriptStateStoreFactory stateStoreFactory, IAdmissionPolicySource? admissionPolicySource, ScriptTombstoneStore tombstones)
    {
        _stateStoreFactory = stateStoreFactory ?? throw new ArgumentNullException(nameof(stateStoreFactory));
        _admissionPolicySource = admissionPolicySource;
        _tombstones = tombstones;
    }

    public ScriptStatusResponse StartScript(StartScriptCommand command)
    {
        // P1-T.11: the pre-fix path
        // threw InvalidOperationException, which Halibut wrapped as a generic
        // RPC failure → server logged a cryptic message and marked the
        // deployment failed. The new contract: return a structured Complete
        // response with the AgentDraining sentinel exit code. Halibut returns
        // it normally, the operator gets an actionable error in stderr, and
        // a future server release can recognise the exit code as retry-able.
        if (_draining) return BuildDrainRejection(command);

        if (command.ScriptTicket == null)
            throw new ArgumentException("StartScriptCommand.ScriptTicket is required for idempotent execution", nameof(command));

        // Admission policy is the agent-side last-line-of-defence. It runs before
        // any idempotency lookup so even a redelivered StartScript is re-evaluated
        // against the current policy (the rules may have tightened since the
        // original submission). Denial returns -403 and never touches the workspace.
        if (TryEvaluateAdmission(command, out var denial))
            return denial!;

        var ticket = command.ScriptTicket;

        return StartOnce(command, () => PeekKnownV1(ticket), () => InFlightStatusV1(ticket, 0), () => LaunchClaimed(command));
    }

    /// <summary>
    /// The one start path for V1 and V2. Under the ticket lock a ticket whose start is in flight answers from that start, a known ticket answers
    /// from what is known, and a new ticket is claimed (in flight) — so two Starts of one ticket, whichever protocol each used, run it once, and a
    /// V1 Cancel or Complete, which decide under the same lock, find it in flight or not, never in between. The launch itself (mutex wait, file
    /// writes, process start) runs outside the lock, see <see cref="LaunchClaimed"/>.
    /// </summary>
    private T StartOnce<T>(StartScriptCommand command, Func<T?> peekKnown, Func<T> inFlight, Func<T> launchClaimed) where T : class
    {
        var ticketId = command.ScriptTicket.TaskId;

        DrainOutputOutsideTicketLock(command.ScriptTicket);

        lock (_ticketLocks.For(ticketId))
        {
            // In flight first: a start ends its claim only after it tracked its script, so a ticket not in flight here is tracked or was never launched.
            if (_startsInFlight.ContainsKey(ticketId)) return inFlight();

            var known = peekKnown();

            if (known != null) return known;

            _startsInFlight[ticketId] = 0;
        }

        return launchClaimed();
    }

    // Under the ticket lock, after the pre-lock drain: the tracked script, else a start a previous agent run persisted.
    private ScriptStatusResponse? PeekKnownV1(ScriptTicket ticket)
    {
        if (_scripts.TryGetValue(ticket.TaskId, out var existing)) return BuildStatus(ticket, existing, drain: false);

        return TryReturnPersistedStatus(ticket, ResolveWorkDir(ticket.TaskId), out var persisted) ? persisted : null;
    }

    // A start still in flight has no output yet: Running with nothing new, as its persisted Starting state answers. Never Complete + UnknownResult,
    // which a V1 server reads as "unknown ticket" and answers by dispatching the script again.
    private static ScriptStatusResponse InFlightStatusV1(ScriptTicket ticket, long lastLogSequence) => new(ticket, ProcessState.Running, 0, new List<ProcessOutput>(), lastLogSequence);

    /// <summary>
    /// The one launch path (V1 and V2 Start). On entry the ticket is in flight; the claim ends once its script is tracked (or the launch failed),
    /// and only then does the starter take a cancel handed to it (see <see cref="EndStartClaim"/>) and carry it out on the tracked script.
    /// </summary>
    private ScriptStatusResponse LaunchClaimed(StartScriptCommand command)
    {
        RunningScript running;
        CancelOrigin? handedCancel;

        try
        {
            running = LaunchTracked(command);
        }
        finally
        {
            handedCancel = EndStartClaim(command.ScriptTicket.TaskId);
        }

        if (handedCancel is { } origin) return CancelHandedToStarter(command.ScriptTicket, origin);

        WaitForEarlyCompletion(running, command.DurationToWaitForScriptToFinish);

        return BuildStatus(command.ScriptTicket, running);
    }

    // Claim first, request second: a Cancel records its request before it re-reads the claim, so either it still sees the ticket in flight and
    // this take finds the request, or it sees the claim gone and takes the request back itself. Whoever removes the request owns the cancel.
    // A failed launch takes (and drops) the request too: there is nothing to cancel.
    private CancelOrigin? EndStartClaim(string ticketId)
    {
        _startsInFlight.TryRemove(ticketId, out _);

        return _cancelRequests.TryRemove(ticketId, out var origin) ? origin : null;
    }

    private ScriptStatusResponse CancelHandedToStarter(ScriptTicket ticket, CancelOrigin origin)
    {
        Log.Information("Cancel requested while ticket {TicketId} was starting, cancelling its script", ticket.TaskId);

        if (origin == CancelOrigin.V2)
        {
            CancelOutsideStartV2(ticket, 0);
            return CompletedResponse(ticket, ScriptExitCodes.Canceled);
        }

        // A later Cancel or Complete may have taken the script meanwhile: it is then theirs to finish.
        return _scripts.TryRemove(ticket.TaskId, out var running) ? CancelRunning(ticket, 0, running) : CompletedResponse(ticket, ScriptExitCodes.Canceled);
    }

    /// <summary>
    /// A ticket being started has nothing this Cancel may take yet: record the request for the starter. True when the starter took the cancel
    /// over; false when no start is in flight (the caller cancels whatever is tracked itself).
    /// </summary>
    private bool TryHandCancelToStarter(ScriptTicket ticket, CancelOrigin origin)
    {
        var ticketId = ticket.TaskId;

        if (!_startsInFlight.ContainsKey(ticketId)) return false;

        // The starter may be waiting for the isolation mutex or writing files: the soft-cancel token cuts that short.
        _cancellationRegistry.Cancel(ticket);

        BeforeCancelRequestRecordedHookForTests?.Invoke();

        _cancelRequests[ticketId] = origin;

        if (_startsInFlight.ContainsKey(ticketId)) return true;

        // The claim ended meanwhile, so the script is tracked (or never launched): take the request back unless the starter already took it.
        _cancelRequests.TryRemove(ticketId, out _);

        return false;
    }

    // Outside the ticket lock: the mutex wait, the workspace and the process start. Returns the script once it is tracked.
    private RunningScript LaunchTracked(StartScriptCommand command)
    {
        DiskSpaceChecker.EnsureDiskHasEnoughFreeSpace(Path.GetTempPath());
        CleanupOrphanedWorkspacesIfDue();

        //  (audit ARCH.9 F1.1) — pure-sync mutex acquire (no sync-over-async). The token is the
        // per-ticket soft-cancel token from the registry: a CancelScript RPC arriving mid-acquire
        // short-circuits the polling loop instead of waiting for the full configured timeout.
        var softCancelToken = _cancellationRegistry.GetOrCreate(command.ScriptTicket);
        var isolationHandle = _isolationMutex.TryAcquireBlocking(command, softCancelToken);

        if (isolationHandle == null)
            throw new InvalidOperationException("Failed to acquire script isolation mutex within the configured timeout");

        StartUnderwayHookForTests?.Invoke();

        return StartTrackedProcess(command, isolationHandle, softCancelToken);
    }

    private RunningScript StartTrackedProcess(StartScriptCommand command, IDisposable? isolationHandle, CancellationToken softCancelToken)
    {
        var ticketId = command.ScriptTicket.TaskId;
        var workDir = ResolveWorkDir(ticketId);

        // P0-T.5 (2026-04-24 audit): ownership of isolationHandle stays with the local until RunningScript is constructed — every
        // step before that can throw, and a leaked handle would hold the mutex forever. Past the construction RunningScript owns
        // it (disposed on Complete / Cancel / error), so the local is nulled and the catch block doesn't double-dispose.
        try
        {
            var stateStore = PrepareWorkspace(workDir, command, softCancelToken);

            AfterFilesWrittenHookForTests?.Invoke();

            var process = StartProcess(workDir, command);
            var logWriter = new SequencedLogWriter(Path.Combine(workDir, "output.log"));
            var running = new RunningScript(process, workDir, isolationHandle!, stateStore, logWriter, command, ResolveParentTraceId(command)) { OutputDrainTimeout = OutputDrainTimeout };

            isolationHandle = null;

            BeginReadOutput(process, running);
            _scripts[ticketId] = running;

            SaveRunningState(stateStore, ticketId, process);

            // MUST come after the Running save above: a script that exits immediately would otherwise have
            // its Complete record overwritten by the stale Running snapshot.
            HookNaturalExitPersistence(process, running);

            Log.Information("Started script {TicketId} in {WorkDir} (syntax: {Syntax})", ticketId, workDir, command.ScriptSyntax);

            return running;
        }
        catch
        {
            isolationHandle?.Dispose();
            throw;
        }
    }

    private IScriptStateStore PrepareWorkspace(string workDir, StartScriptCommand command, CancellationToken softCancelToken)
    {
        Directory.CreateDirectory(workDir);
        SetDirectoryPermissions(workDir);

        var stateStore = _stateStoreFactory.Create(workDir);
        stateStore.Save(new ScriptState { TicketId = command.ScriptTicket.TaskId, Progress = ScriptProgress.Starting, CreatedAt = DateTimeOffset.UtcNow });

        WriteScriptFile(workDir, command.ScriptBody, command.ScriptSyntax);

        // : thread the per-ticket soft-cancel token so a CancelScript RPC arriving mid-write actually aborts.
        WriteAdditionalFiles(workDir, command.Files, softCancelToken);

        return stateStore;
    }

    private static void SaveRunningState(IScriptStateStore stateStore, string ticketId, Process process)
    {
        stateStore.Save(new ScriptState
        {
            TicketId = ticketId,
            Progress = ScriptProgress.Running,
            ProcessId = process.Id,
            // OS-reported start time captured alongside the PID so orphan-state detection (TryBuildStatusFromPersistedLogs)
            // can tell "same process still running" from "PID got recycled to an unrelated process".
            ProcessStartedAt = TryGetProcessStartTime(process),
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow
        });
    }

    private static string? ResolveParentTraceId(StartScriptCommand command)
    {
        // Server propagates W3C traceparent through command metadata when present.
        // We don't currently transport it over the wire (ScriptExecutionTrace is
        // ready for it once StartScriptCommand gains a TraceContext field). For
        // now, fall back to Activity.Current?.TraceId which will be set when the
        // agent itself happens to be running inside a traced context.
        return Activity.Current?.TraceId.ToString();
    }

    private bool TryEvaluateAdmission(StartScriptCommand command, out ScriptStatusResponse? denial)
    {
        denial = null;
        var policy = _admissionPolicySource?.Current;
        if (policy == null || policy.Rules.Count == 0) return false;

        AdmissionDecision decision;
        try
        {
            decision = policy.Evaluate(new AdmissionContext(
                command.ScriptBody,
                command.Isolation.ToString(),
                command.IsolationMutexName));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Admission policy evaluation failed; failing closed (denying script)");
            denial = BuildAdmissionDenial(command.ScriptTicket, "policy-eval-error",
                "admission policy evaluation failed — agent fails closed to avoid running unreviewed scripts");
            return true;
        }

        if (decision.Allowed) return false;

        Log.Warning("Admission denied for ticket {TicketId} by rule {RuleId}: {Reason}",
            command.ScriptTicket.TaskId, decision.RuleId, decision.Reason);
        denial = BuildAdmissionDenial(command.ScriptTicket, decision.RuleId ?? "unknown", decision.Reason ?? "denied");
        return true;
    }

    private static ScriptStatusResponse BuildAdmissionDenial(ScriptTicket ticket, string ruleId, string reason)
    {
        return new ScriptStatusResponse(ticket, ProcessState.Complete, -403,
            new List<ProcessOutput>
            {
                new(ProcessOutputSource.StdErr, $"Admission denied by rule '{ruleId}': {reason}")
            },
            0);
    }

    private bool TryReturnPersistedStatus(ScriptTicket ticket, string workDir, out ScriptStatusResponse? response)
    {
        response = null;

        var stateStore = _stateStoreFactory.Create(workDir);
        if (!stateStore.Exists()) return false;

        ScriptState state;
        try { state = stateStore.Load(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "State file at {WorkDir} exists but is unreadable — treating as missing", workDir);
            return false;
        }

        if (!state.HasStarted()) return false;

        Log.Information("Redelivered StartScript for ticket {TicketId} — returning persisted state {Progress}",
            ticket.TaskId, state.Progress);

        response = BuildStatusFromPersistedState(ticket, state);
        return true;
    }

    private static ScriptStatusResponse BuildStatusFromPersistedState(ScriptTicket ticket, ScriptState state)
    {
        var processState = state.Progress == ScriptProgress.Complete
            ? ProcessState.Complete
            : ProcessState.Running;
        var exitCode = state.ExitCode ?? 0;

        return new ScriptStatusResponse(ticket, processState, exitCode, new List<ProcessOutput>(), state.NextLogSequence);
    }

    /// <summary>
    /// Strict whitelist for ticket IDs embedded in the workspace directory name.
    /// Accepts only alphanumerics + hyphen + underscore, 1-64 chars.
    /// <para>Why narrow: the server embeds <c>ticketId</c> straight into a
    /// filesystem path under <c>/tmp</c> via <see cref="ResolveWorkDir"/>. A
    /// value containing <c>..</c>, <c>/</c>, <c>\\</c>, null bytes, or
    /// whitespace lets a compromised-server or a log-spoofed input escape
    /// the temp dir and write agent state / script bodies / logs to
    /// arbitrary host paths. The <c>squid-tentacle-</c> prefix inoculates
    /// against bare-absolute-path injection (Path.Combine wins with absolute
    /// second arg, but the literal prefix makes the concatenation
    /// non-absolute) — it does NOT prevent <c>../../</c> segments, which
    /// normalise past the prefix and out of /tmp.</para>
    /// <para>Why 64 chars: the longest legitimate ticket shape is a SHA-256
    /// hex string (64 chars). Guid N-format is 32. A 65-char ticket is
    /// either a bug or an attempt to hit filesystem path-length limits.</para>
    /// <para>Pinned by <c>ResolveWorkDir_MaliciousTicketId_Throws</c> +
    /// <c>ResolveWorkDir_LegitimateTicketId_ReturnsPathUnderTemp</c>.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TicketIdWhitelist =
        new("^[a-zA-Z0-9_-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string ResolveWorkDir(string ticketId)
    {
        if (ticketId == null || !TicketIdWhitelist.IsMatch(ticketId))
            throw new ArgumentException(
                $"Invalid ticketId (got: '{ticketId ?? "<null>"}'). Must match {TicketIdWhitelist} — " +
                "alphanumerics + hyphen + underscore, 1-64 chars. Path-traversal, whitespace, " +
                "null bytes, and non-ASCII characters are rejected to prevent workspace-dir " +
                "escape attacks.",
                nameof(ticketId));

        return Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
    }

    private static void WaitForEarlyCompletion(RunningScript running, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        try { running.Process.WaitForExit(duration); }
        catch { /* Best-effort early return — polling will catch final state */ }
    }

    private ScriptStatusResponse BuildStatus(ScriptTicket ticket, RunningScript running, long afterSequence = -1, bool drain = true)
    {
        // Exit is read ONCE, before draining. Reading it after the log file let a process that exited in between be
        // reported Complete with logs read while it was still running, i.e. without its final lines.
        var exited = running.Process.HasExited;

        // Drain AsyncStreamReader callbacks on the FIRST poll that observes
        // HasExited=true. Without this, GetStatus can return state=Complete
        // with stdout lines still in flight on the threadpool — the line is
        // already in the kernel pipe and the AsyncStreamReader has read it,
        // but the OutputDataReceived event hasn't been dispatched yet, so it
        // isn't in the on-disk log file when ReadLogsAndCursor reads below.
        // The test then sees "ExitCode=0 + last 1-2 log lines missing".
        //
        // CompleteScript already does this drain on its own teardown path
        // (line ~594), but GetStatus is the OTHER way state=Complete becomes
        // observable — when the test polls before calling CompleteScript.
        // PR #317's RunToCompletion accumulator papered over this by polling
        // additional times after seeing Complete; this fix removes the race
        // at its source so single-poll callers also get the full output.
        //
        // Idempotent and bounded — see AwaitOutputDrained.
        if (drain) AwaitOutputDrained(running);

        // Without a drain (under a ticket lock), "exited" is only trustworthy once the output is complete; otherwise the final lines could be missing from a Complete.
        if (!drain && !running.OutputDrained) exited = false;

        // Cursor MUST be derived from the same disk read as `logs` — see
        // ReadLogsAndCursor's XML for the race that the previous two-source
        // code (ReadLogs + LogWriter.NextSequence) opened.
        var (logs, nextSequence) = ReadLogsAndCursor(running, afterSequence);

        StatusLogsReadHookForTests?.Invoke();

        var state = exited ? ProcessState.Complete : ProcessState.Running;
        var exitCode = exited ? running.Process.ExitCode : 0;

        return new ScriptStatusResponse(ticket, state, exitCode, logs, nextSequence);
    }

    /// <summary>
    /// Waits (bounded) for the script's stdout/stderr readers to reach EOF once its process has exited, so the final
    /// output lines are on disk before the result is read or recorded. No-op while the process is still running.
    ///
    /// <para>Deliberately NOT the parameterless <see cref="Process.WaitForExit()"/>: that waits until EVERY holder of the
    /// pipe closes it, so a script that leaves a background child (<c>nohup app &amp;</c>) blocks the caller until that child
    /// exits. EOF is tracked from the readers' own end-of-stream callbacks instead; if it has not arrived
    /// <see cref="RunningScript.OutputDrainTimeout"/> after the exit was first observed, reading stops, what was captured is
    /// kept and a truncation line is appended. The wait is anchored at the first observed exit, so concurrent and later
    /// callers share one deadline and never add up.</para>
    ///
    /// <para>Never call this while holding a ticket lock: it can block for up to the drain timeout.</para>
    /// </summary>
    private static void AwaitOutputDrained(RunningScript running)
    {
        if (running.OutputDrained || IsProcessRunning(running)) return;

        running.NoteExitObserved();

        lock (running.DrainGate)
        {
            if (running.OutputDrained) return;

            if (!running.OutputEof.Task.Wait(running.RemainingDrainBudget()))
                StopReadingOutput(running);

            running.OutputDrained = true;
        }
    }

    private static void StopReadingOutput(RunningScript running)
    {
        Log.Warning("Script output pipe still open {Seconds}s after exit (a background process kept it); stopping output capture for {WorkDir}", running.OutputDrainTimeout.TotalSeconds, running.WorkDir);

        try
        {
            running.Process.CancelOutputRead();
            running.Process.CancelErrorRead();
        }
        catch (InvalidOperationException)
        {
            // Readers were never started or the process object is already released: nothing left to stop.
        }

        running.LogWriter?.Append(ProcessOutputSource.StdErr, OutputTruncatedMessage);
    }

    internal const string OutputTruncatedMessage = "[squid] output truncated: a background process kept the output pipe open after the script exited";

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        // Flag first: a start ends its claim only after it tracked its script, so reading in this order cannot miss a start that finishes while we look.
        var startInFlight = _startsInFlight.ContainsKey(request.Ticket.TaskId);

        if (_scripts.TryGetValue(request.Ticket.TaskId, out var running))
            return BuildStatus(request.Ticket, running, request.LastLogSequence - 1);

        if (startInFlight) return InFlightStatusV1(request.Ticket, request.LastLogSequence);

        var workDir = ResolveWorkDir(request.Ticket.TaskId);

        if (TryBuildStatusFromPersistedLogs(request.Ticket, workDir, request.LastLogSequence, out var persisted))
            return persisted!;

        return CompletedResponse(request.Ticket, ScriptExitCodes.UnknownResult);
    }

    private bool TryBuildStatusFromPersistedLogs(ScriptTicket ticket, string workDir, long lastSeq, out ScriptStatusResponse? response)
    {
        response = null;

        var stateStore = _stateStoreFactory.Create(workDir);
        if (!stateStore.Exists()) return false;

        ScriptState state;
        try { state = stateStore.Load(); }
        catch { return false; }

        // Single disk read: returns (logs, derivedNextSeq) consistent with each
        // other. The previous code did `ReadFrom` here and a separate
        // `GetHighestSequence` later — a concurrent writer between the two reads
        // (e.g. while the orphan tentacle is still emitting output during a
        // mid-restart poll) advanced the cursor past entries the first read
        // didn't see, so the next poll skipped them. See ReadLogsAndCursor.
        var (logs, derivedNextSeq) = ReadLogsAndCursor(Path.Combine(workDir, "output.log"), lastSeq - 1);

        // Orphan-state detection (1.6.x fix): if the state file says
        // Progress=Running but the recorded process is gone or has been
        // recycled, we're looking at an abandoned script — likely its
        // owning tentacle was killed (e.g. Phase B of a self-upgrade
        // restarted the tentacle while its own script was "in progress").
        // Without this check, the server's Halibut script observer would
        // poll GetStatus forever (state says Running), HTTP dispatch
        // request hangs for the full 5-min timeout, Redis upgrade lock
        // stays held, operator's UI shows a spinner that never resolves.
        //
        // Detection: check if state.ProcessId still points to the SAME
        // process (PID alive AND start time matches the recorded value).
        // Covers two failure modes:
        //   (a) PID dead: Process.GetProcessById throws → !alive → Complete
        //   (b) PID recycled: a long-running tentacle eventually sees the
        //       OS reassign its dead script's PID to an unrelated process.
        //       ProcessStartedAt cross-check fails → !alive → Complete
        //
        // Backward-compat: pre-1.6.x state files lack ProcessStartedAt
        // (null) — the predicate falls back to PID-only liveness, matching
        // the original behaviour the field didn't exist.
        if (!state.IsComplete() && state.ProcessId.HasValue && state.ProcessId.Value > 0 && !IsSameProcessAlive(state.ProcessId.Value, state.ProcessStartedAt))
        {
            Log.Information(
                "[LocalScriptService] Orphan script detected: ticket {TicketId} had Progress=Running " +
                "with ProcessId {ProcessId} (recorded start {RecordedStart}) but that PID is either " +
                "no longer alive or has been recycled to a different process. Reporting as Complete " +
                "with UnknownResult so the server-side observer can release its lock instead of " +
                "polling forever.",
                ticket.TaskId, state.ProcessId.Value, state.ProcessStartedAt);

            // Orphan: trust the disk-derived cursor over state.NextLogSequence —
            // an orphaned writer may have appended past the last persisted save.
            response = new ScriptStatusResponse(ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult, logs, derivedNextSeq);
            return true;
        }

        var processState = state.IsComplete() ? ProcessState.Complete : ProcessState.Running;
        var exitCode = state.ExitCode ?? 0;

        // For a Complete script, state.NextLogSequence was set atomically with
        // PersistCompleteState — that snapshot is the canonical final cursor.
        // For a Running script (still emitting output via the original agent
        // process), state.NextLogSequence is 0 (never written during Running)
        // and we MUST use derivedNextSeq from the SAME read as `logs` to keep
        // the cursor consistent with what the caller is about to see.
        var nextSeq = state.IsComplete() && state.NextLogSequence > 0
            ? state.NextLogSequence
            : derivedNextSeq;

        response = new ScriptStatusResponse(ticket, processState, exitCode, logs, nextSeq);
        return true;
    }

    /// <summary>
    /// Best-effort liveness check for a PID the current process doesn't own,
    /// with OPTIONAL start-time cross-check to defend against PID recycling.
    ///
    /// <para>If <paramref name="expectedStartedAt"/> is null (legacy state
    /// files that predate the start-time capture), falls back to PID-only
    /// liveness — same behaviour as the pre-1.6.x implementation.</para>
    ///
    /// <para>If <paramref name="expectedStartedAt"/> is supplied, the
    /// process's actual <see cref="System.Diagnostics.Process.StartTime"/>
    /// must match within a 2-second tolerance for the process to be
    /// considered "still the same one that was recorded". A wider gap means
    /// the PID has been recycled — the original script is gone, the current
    /// occupant is unrelated. Tolerance covers clock rounding between
    /// <c>Process.StartTime</c> (OS-reported) and <c>DateTimeOffset.UtcNow</c>
    /// (caller's capture time); never seen more than ~50 ms drift in
    /// practice, but 2 s leaves ample margin.</para>
    ///
    /// <para>Returns false on ANY exception (GetProcessById throws for
    /// missing PID, StartTime throws for unreadable /proc entries, etc.)
    /// — orphan-state detection errs on the side of releasing the
    /// server-side observer. Losing a live script to a spurious "dead"
    /// verdict is strictly better than hanging the observer forever on
    /// a recycled one.</para>
    /// </summary>
    private static bool IsSameProcessAlive(int processId, DateTimeOffset? expectedStartedAt)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(processId);

            if (proc.HasExited) return false;

            // Legacy state-file path: no recorded start time → PID-only check.
            if (!expectedStartedAt.HasValue) return true;

            var actualStart = proc.StartTime.ToUniversalTime();
            var recorded = expectedStartedAt.Value.ToUniversalTime();

            // 2-second tolerance — covers OS-reported-vs-caller-captured
            // rounding skew without admitting a genuinely recycled PID.
            return Math.Abs((actualStart - recorded).TotalSeconds) <= 2.0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort capture of a freshly-spawned process's start time so
    /// orphan-state detection can later verify the PID hasn't been recycled.
    /// Returns null on any failure (process already exited between Start
    /// and GetStartTime, /proc unreadable, etc.) — the resulting state
    /// file will lack the field and orphan detection falls back to
    /// PID-only liveness for that script.
    /// </summary>
    private static DateTimeOffset? TryGetProcessStartTime(System.Diagnostics.Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        //  audit follow-up (#2): null-ticket NRE defence. Pre-fix
        // a malformed RPC with null Ticket caused NullReferenceException
        // inside the registry's TaskId access — Halibut wrapped as opaque
        // RPC failure. Fail explicitly so the operator gets a structured
        // error message naming the actual problem.
        if (command.Ticket == null)
            throw new ArgumentException("CompleteScriptCommand.Ticket is required", nameof(command));

        var running = TakeForCompleteV1(command, out var notRunning);

        return running == null ? notRunning! : CompleteRunning(command.Ticket, command.LastLogSequence, running);
    }

    /// <summary>
    /// Under the ticket lock, serialized with Start's claim: the tracked script is taken; a ticket whose start is in flight keeps its Starting
    /// state and soft-cancel token (the starter still needs both) and gets the unchanged V1 answer for nothing to release; any other ticket is
    /// released from disk.
    /// </summary>
    private RunningScript? TakeForCompleteV1(CompleteScriptCommand command, out ScriptStatusResponse? notRunning)
    {
        notRunning = null;

        lock (_ticketLocks.For(command.Ticket.TaskId))
        {
            var startInFlight = _startsInFlight.ContainsKey(command.Ticket.TaskId);

            if (_scripts.TryRemove(command.Ticket.TaskId, out var running)) return running;

            notRunning = startInFlight ? CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult) : ReleaseNotRunningV1(command);

            return null;
        }
    }

    private ScriptStatusResponse ReleaseNotRunningV1(CompleteScriptCommand command)
    {
        var workDir = ResolveWorkDir(command.Ticket.TaskId);
        var recovered = RecoverPersistedCompletion(command, workDir);
        DeletePersistedStateIfAny(workDir);
        //  audit follow-up (#1): CompleteScript early-return
        // path was missing _cancellationRegistry.Cleanup. The leak
        // path: StartScript registers with the registry BEFORE
        // _scripts.TryAdd; if a failure between those steps leaves
        // the registry populated but _scripts empty, a subsequent
        // CompleteScript hits this branch — pre-fix the registry
        // entry leaked. Now matches the CancelScript pattern
        // (Cleanup on every exit path). Test verifies the call site
        // is executed; full leak-path repro requires mid-Start
        // failure injection that's not in scope for this phase.
        _cancellationRegistry.Cleanup(command.Ticket);
        return recovered ?? CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult);
    }

    private ScriptStatusResponse CompleteRunning(ScriptTicket ticket, long lastLogSequence, RunningScript running, int? forcedExitCode = null, Func<int, bool>? onFinalExitCode = null, TimeSpan? exitWait = null)
    {
        if (!running.Process.HasExited)
            running.Process.WaitForExit(exitWait ?? TimeSpan.FromSeconds(30));

        // ALWAYS drain the output readers before reading logs, even if the process exited before this call: the
        // timed WaitForExit above does not wait for them, and a late callback would hit a disposed LogWriter.
        // Bounded — see AwaitOutputDrained.
        AwaitOutputDrained(running);

        var logs = ReadLogs(running, lastLogSequence - 1);
        var exitCode = forcedExitCode ?? (running.Process.HasExited ? running.Process.ExitCode : ScriptExitCodes.Timeout);

        FinalizeState(running, exitCode);

        // Write the execution manifest BEFORE workspace cleanup so the audit
        // artifact is guaranteed to hit disk even if cleanup aborts. The manifest
        // is useful to operators doing post-mortems even after the workspace
        // itself is gone — it gets copied out to a durable archive externally.
        WriteExecutionManifest(running, ticket.TaskId, exitCode);

        // The hook returns false when the result could not be recorded elsewhere (tombstone write failed): the state file
        // in the workspace is then the only record, so the workspace must stay.
        var releaseWorkspace = onFinalExitCode?.Invoke(exitCode) ?? true;

        running.LogWriter?.Dispose();
        running.IsolationHandle?.Dispose();

        if (releaseWorkspace)
            CleanupWorkDir(running.WorkDir);
        else
            StateOnlyWorkspace.Scrub(running.WorkDir);

        running.Process.Dispose();

        // : release the per-ticket soft-cancel CTS now the
        // script has reached a terminal state. Without this, the registry
        // accumulates one CTS per script ever run — small leak but
        // unbounded over agent lifetime.
        _cancellationRegistry.Cleanup(ticket);

        return new ScriptStatusResponse(ticket, ProcessState.Complete, exitCode, logs, running.LogSequence);
    }

    /// <summary>
    /// After an agent restart the in-memory entry is gone but the state file may record a real
    /// completion (see <see cref="PersistNaturalExit"/>). Returns that result with the on-disk logs
    /// so CompleteScript does not downgrade a recorded outcome to UnknownResult; null otherwise.
    /// </summary>
    private ScriptStatusResponse? RecoverPersistedCompletion(CompleteScriptCommand command, string workDir)
    {
        if (!TryBuildStatusFromPersistedLogs(command.Ticket, workDir, command.LastLogSequence, out var persisted)) return null;
        if (persisted!.State != ProcessState.Complete || persisted.ExitCode == ScriptExitCodes.UnknownResult) return null;

        return persisted;
    }

    private static void WriteExecutionManifest(RunningScript running, string ticketId, int exitCode)
    {
        if (running.Command == null) return;

        try
        {
            var manifest = ExecutionManifest.Build(
                ticketId,
                running.Command,
                AgentVersion,
                running.StartedAt,
                exitCode,
                DateTimeOffset.UtcNow,
                running.TraceId,
                running.WorkDir);
            manifest.WriteTo(running.WorkDir);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to write execution manifest for ticket {TicketId}", ticketId);
        }
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command)
    {
        //  audit follow-up (#2): null-ticket NRE defence. Pre-fix
        // a malformed RPC with null Ticket caused NullReferenceException
        // inside the registry's TaskId access. Match StartScript's pattern.
        if (command.Ticket == null)
            throw new ArgumentException("CancelScriptCommand.Ticket is required", nameof(command));

        //  (audit ARCH.9 F2.x soft-cancel): flip the registry's
        // CTS BEFORE attempting to remove from _scripts. This signals any
        // in-flight async work (mutex acquire, file save) that's NOT yet
        // tracked in _scripts — a CancelScript that races a slow StartScript
        // can short-circuit it via the soft-cancel token. The early-cancel
        // sentinel pattern (see ScriptCancellationRegistry.Cancel) handles
        // the out-of-order case where Cancel arrives BEFORE the matching
        // GetOrCreate.
        _cancellationRegistry.Cancel(command.Ticket);

        var running = ClaimForCancelV1(command.Ticket);

        return running == null ? CompletedResponse(command.Ticket, ScriptExitCodes.Canceled) : CancelRunning(command.Ticket, command.LastLogSequence, running);
    }

    /// <summary>
    /// Under the ticket lock, serialized with Start's claim: a start in flight takes the cancel over (null), a tracked script is taken, and only a
    /// ticket that is neither has its persisted state and soft-cancel token released (null) — never the state a start in flight is about to use.
    /// </summary>
    private RunningScript? ClaimForCancelV1(ScriptTicket ticket)
    {
        lock (_ticketLocks.For(ticket.TaskId))
        {
            if (TryHandCancelToStarter(ticket, CancelOrigin.V1)) return null;

            if (_scripts.TryRemove(ticket.TaskId, out var running)) return running;

            DeletePersistedStateIfAny(ResolveWorkDir(ticket.TaskId));
            _cancellationRegistry.Cleanup(ticket);

            return null;
        }
    }

    private ScriptStatusResponse CancelRunning(ScriptTicket ticket, long lastLogSequence, RunningScript running)
    {
        // Cancel wins over a racing natural exit: the server asked for it and is told Canceled, so
        // the recorded result must say Canceled too. Flag BEFORE Kill so the exit callback (which
        // would otherwise record the kill signal's exit code) stands down.
        MarkCancelRequested(running);

        try
        {
            if (!running.Process.HasExited)
                running.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill process for ticket {TicketId}", ticket.TaskId);
        }

        // Drain the output readers (same rationale as CompleteScript). Kill is best-effort, so first give the
        // process a brief moment to go away.
        try
        {
            if (!running.Process.HasExited)
                running.Process.WaitForExit(TimeSpan.FromSeconds(5));

            AwaitOutputDrained(running);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to wait for process exit on cancel for ticket {TicketId}", ticket.TaskId);
        }

        var logs = ReadLogs(running, lastLogSequence - 1);

        FinalizeState(running, ScriptExitCodes.Canceled);

        running.LogWriter?.Dispose();
        running.IsolationHandle?.Dispose();
        CleanupWorkDir(running.WorkDir);
        running.Process.Dispose();

        // : dispose the per-ticket CTS now the script has been
        // cancelled. Idempotent — safe even if Cancel was a no-op above.
        _cancellationRegistry.Cleanup(ticket);

        return new ScriptStatusResponse(ticket, ProcessState.Complete, ScriptExitCodes.Canceled, logs, running.LogSequence);
    }

    private static void PersistCompleteState(RunningScript running, int exitCode)
    {
        if (running.StateStore == null) return;

        try
        {
            var existing = running.StateStore.Exists() ? running.StateStore.Load() : new ScriptState();
            existing.Progress = ScriptProgress.Complete;
            existing.ExitCode = exitCode;
            existing.CompletedAt = DateTimeOffset.UtcNow;
            existing.NextLogSequence = running.LogSequence;
            running.StateStore.Save(existing);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to persist complete state for {WorkDir}", running.WorkDir);
        }
    }

    private void DeletePersistedStateIfAny(string workDir)
    {
        try
        {
            var store = _stateStoreFactory.Create(workDir);
            if (store.Exists()) store.Delete();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to delete persisted state for {WorkDir}", workDir);
        }
    }

    /// <summary>
    /// Build the structured rejection response for a StartScript call that
    /// arrived after the drain flag flipped. Returns Complete + AgentDraining
    /// + an operator-readable stderr explanation. See P1-T.11 comment in
    /// <see cref="StartScript"/> for the rationale.
    /// </summary>
    private ScriptStatusResponse BuildDrainRejection(StartScriptCommand command)
    {
        var ticketId = command.ScriptTicket?.TaskId ?? "<no-ticket>";

        // Surface the rejection in the agent's own logs at Warning so SREs
        // can grep for drain-window dispatches alongside the operator-side
        // signal that the deployment ran into the agent's drain.
        Log.Warning(
            "Rejecting StartScript for ticket {TicketId} — tentacle is shutting down (drain mode active). " +
            "Returning AgentDraining ({ExitCode}); deployment should be retried.",
            ticketId, ScriptExitCodes.AgentDraining);

        var stderr = new ProcessOutput(
            ProcessOutputSource.StdErr,
            "Tentacle is shutting down and rejected this script for graceful drain. The script did not run; please retry.");

        return new ScriptStatusResponse(
            command.ScriptTicket,
            ProcessState.Complete,
            ScriptExitCodes.AgentDraining,
            new List<ProcessOutput> { stderr },
            nextLogSequence: 0);
    }

    /// <summary>
    /// A script that creates this file in its working directory declares itself detached from the
    /// agent's lifecycle (the upgrade script hands off to a systemd scope and then restarts the agent):
    /// the drain neither waits for it nor kills it. Renaming breaks the shipped upgrade script, so the
    /// literal is pinned by a test.
    /// </summary>
    public const string DrainExemptMarkerFileName = ".squid-drain-exempt";

    public async Task WaitForDrainAsync(TimeSpan timeout)
    {
        _draining = true;
        Log.Information("LocalScriptService drain started. {Count} script(s) active", _scripts.Count);

        await WaitForScriptProcessesToExitAsync(timeout).ConfigureAwait(false);

        PersistExitedScripts();
        KillRemainingScripts();

        Log.Information("LocalScriptService drain complete");
    }

    // Polling is cancelled before the drain, so no CompleteScript/CancelScript RPC can arrive to
    // empty _scripts; the drain therefore waits on the PROCESSES, not on the registry.
    private async Task WaitForScriptProcessesToExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            while (_scripts.Values.Any(HoldsDrain))
                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout — remaining scripts are force-killed by the caller
        }
    }

    // Synchronously writes the real exit code of every script that exited, so it is on disk before
    // the agent process ends (the async Exited callback may not have run yet). Idempotent.
    private void PersistExitedScripts()
    {
        foreach (var running in _scripts.Values.Where(r => !IsProcessRunning(r)))
            PersistNaturalExit(running);
    }

    private void KillRemainingScripts()
    {
        foreach (var (ticketId, running) in _scripts)
        {
            try
            {
                if (!HoldsDrain(running)) continue;

                Log.Warning("Drain timeout — killing script {TicketId}", ticketId);

                // The kill signal's exit code is not a result: suppress natural-exit persistence so the
                // state stays Running and a restarted agent reports UnknownResult (resumable), as before.
                MarkCancelRequested(running);
                running.Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to kill script {TicketId} during drain", ticketId);
            }
        }
    }

    private static bool HoldsDrain(RunningScript running) => IsProcessRunning(running) && !IsDrainExempt(running);

    private static bool IsDrainExempt(RunningScript running) => File.Exists(Path.Combine(running.WorkDir, DrainExemptMarkerFileName));

    private static bool IsProcessRunning(RunningScript running)
    {
        try
        {
            return !running.Process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ========================================================================
    // Script File Writing
    // ========================================================================

    internal static void WriteScriptFile(string workDir, string scriptBody, ScriptType syntax)
    {
        var scriptPath = Path.Combine(workDir, ScriptFileNameFor(syntax));

        var content = syntax == ScriptType.PowerShell
            ? PowerShellUtf8Preamble + scriptBody
            : scriptBody;

        File.WriteAllText(scriptPath, content, EncodingFor(syntax));

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    /// <summary>
    /// Forces UTF-8 stdout encoding inside the spawned PowerShell process.
    ///
    /// <para><b>Why this is needed:</b> on Windows, both <c>powershell.exe</c> 5.1
    /// (always) AND <c>pwsh.exe</c> 7.x (in some host configurations on
    /// GHA <c>windows-latest</c> + non-en-US locales) default <c>$OutputEncoding</c>
    /// and/or <c>[Console]::OutputEncoding</c> to the host's OEM codepage
    /// (cp437 / cp936 / cp932 …). When a script does <c>Write-Output '你好'</c>,
    /// PowerShell encodes the chars via that OEM codepage; chars not representable
    /// in the codepage become <c>?</c>. The .NET parent process then decodes the
    /// already-mangled bytes via <see cref="ProcessStartInfo.StandardOutputEncoding"/>
    /// — but the chars are gone before they hit the pipe.</para>
    ///
    /// <para><b>The fix:</b> prepend a one-liner that hard-pins both encoding
    /// variables to BOM-less UTF-8 BEFORE the user's script runs. Cheap, idempotent,
    /// safe on every PowerShell host (Windows + Linux pwsh-Core + macOS pwsh-Core).
    /// Single-line keeps script line-number shift at exactly 2 (header comment + setter)
    /// so error messages from operator scripts still point at meaningful lines.</para>
    ///
    /// <para><b>Pinned by</b> <c>Squid.Tentacle.Tests.ScriptExecution.WindowsPowerShellE2ETests
    /// .Pwsh_UnicodeOutput_EncodedCorrectly</c> (Chinese chars round-trip through the captured
    /// log) — the test that surfaced this bug on the GHA windows-latest runner.</para>
    /// </summary>
    internal const string PowerShellUtf8Preamble =
        "# Squid: force UTF-8 stdout so non-ASCII chars (Chinese / emoji) round-trip through the captured-log layer.\n" +
        "$OutputEncoding=[System.Text.UTF8Encoding]::new($false);[Console]::OutputEncoding=$OutputEncoding\n";

    /// <summary>
    /// Canonical on-disk filename for a given script syntax. Each interpreter
    /// expects its own extension (Python's <c>importlib</c> resolution, dotnet-
    /// script's <c>.csx</c> recognition, dotnet fsi's <c>.fsx</c>); writing them
    /// all to <c>script.sh</c> works only by accident for some interpreters and
    /// breaks others.
    /// </summary>
    internal static string ScriptFileNameFor(ScriptType syntax) => syntax switch
    {
        ScriptType.PowerShell => "script.ps1",
        ScriptType.Python => "script.py",
        ScriptType.CSharp => "script.csx",
        ScriptType.FSharp => "script.fsx",
        _ => "script.sh"
    };

    /// <summary>
    /// .ps1 needs UTF-8 with BOM for Windows PowerShell 5.1 to parse non-ASCII
    /// characters; everything else uses BOM-less UTF-8 (Python is BOM-tolerant
    /// from 3.0+, but bash, dotnet-script and fsi all dislike BOM at the start).
    /// </summary>
    private static UTF8Encoding EncodingFor(ScriptType syntax) => syntax switch
    {
        ScriptType.PowerShell => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    };

    internal static void WriteAdditionalFiles(string workDir, List<ScriptFile> files, CancellationToken cancellationToken = default)
    {
        if (files == null) return;

        var resolvedWorkDir = Path.GetFullPath(workDir);

        foreach (var file in files)
        {
            //  (audit ARCH.9 F1.2): observe soft-cancel BETWEEN
            // files. CancelScript arriving mid-write of a 1GB sensitiveVars
            // payload now short-circuits the loop instead of proceeding to
            // the next file. The per-file SaveToAsync ALSO threads the CT
            // (line below) so a cancel mid-stream aborts even one large
            // file's transfer.
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.GetFullPath(Path.Combine(workDir, file.Name));

            if (!filePath.StartsWith(resolvedWorkDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && filePath != resolvedWorkDir)
                throw new InvalidOperationException($"File path '{file.Name}' escapes the workspace directory");

            var fileDir = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
                Directory.CreateDirectory(fileDir);

            var tempPath = Path.GetTempFileName();

            try
            {
                //  (audit ARCH.9 F1.2): pass the per-ticket
                // soft-cancel token through to the DataStream receiver.
                //  this was hardcoded CancellationToken.None
                // — a 1GB sensitiveVariables.json payload would write to
                // completion regardless of CancelScript. Now mid-stream
                // cancel aborts the SaveToAsync via the underlying
                // CopyToAsync's CT plumbing.
                //
                // Wrapped in SaveDataStreamWithRetry to absorb the transient
                // IOException from Windows Defender / AV holding the source
                // temp file open during real-time scan. See helper doc-comment
                // for the failure mode this addresses.
                SaveDataStreamWithRetry(file, tempPath, cancellationToken);

                File.Move(tempPath, filePath, overwrite: true);

                if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && !IsScriptFile(file.Name))
                    File.SetUnixFileMode(filePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

                // P0-B.2 (2026-04-24 audit): the old `.key` sidecar file co-located the
                // password with the ciphertext on disk — defeating encryption-at-rest for
                // disk snapshots / backups / offline compromise. Password now only lives
                // in memory on the ScriptFile instance and rides to the Calamari child
                // process via env var (see BuildCalamariProcessStartInfo).
            }
            finally
            {
                // P0-T.7 (2026-04-24 audit): cleanup must always run, but the exception
                // must bubble up to the caller. Pre-fix this was `catch { cleanup; }` with
                // no rethrow — write failures silently disappeared and the script ran
                // against an incomplete workspace. Empty catch reopens that vector; use
                // finally + implicit rethrow instead.
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
    }

    private static bool IsScriptFile(string fileName)
        => fileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum SaveDataStreamWithRetry attempts. Five attempts × exponential
    /// backoff (100 / 200 / 400 / 800 ms, max 1000 ms) tops out at ~2.5s total
    /// wait. Real Windows Defender scan windows are typically &lt;300 ms; five
    /// attempts is generous headroom.
    /// </summary>
    internal const int SaveDataStreamMaxAttempts = 5;

    /// <summary>
    /// Initial inter-attempt delay (ms) for SaveDataStreamWithRetry. Doubles
    /// per attempt up to <see cref="SaveDataStreamMaxDelayMs"/>.
    /// </summary>
    internal const int SaveDataStreamInitialDelayMs = 100;

    /// <summary>Cap on the per-attempt delay (ms). Above this point we hold steady.</summary>
    internal const int SaveDataStreamMaxDelayMs = 1000;

    /// <summary>
    /// Env var override for testing / air-gapped operators with aggressive AV.
    /// When set to a positive integer, replaces <see cref="SaveDataStreamMaxAttempts"/>.
    /// Pinned name (Rule 8): tests assert the literal string.
    /// </summary>
    internal const string SaveDataStreamMaxAttemptsEnvVar = "SQUID_TENTACLE_SAVE_DATASTREAM_MAX_ATTEMPTS";

    /// <summary>
    /// Wraps Halibut <c>SaveToAsync</c> in a retry-with-exponential-backoff
    /// loop. Absorbs transient <see cref="IOException"/> with the "being used
    /// by another process" pattern — typically Windows Defender / AV real-time
    /// scan holding the source temp file open briefly after Halibut writes it.
    ///
    /// <para><b>Failure mode this fixes</b> (real production report):</para>
    ///
    /// <code>
    ///   IOException: The process cannot access the file 'C:\Windows\TEMP\{guid}_1'
    ///   because it is being used by another process.
    ///     at Halibut.Transport.Protocol.TemporaryFileStream.SaveToAsync(string filePath, ...)
    ///     at Squid.Tentacle.ScriptExecution.LocalScriptService.WriteAdditionalFiles(...)
    /// </code>
    ///
    /// <para>The locked file is Halibut's INTERNAL backing temp where the
    /// inbound DataStream was buffered. Right after Halibut closes its write
    /// handle, Windows Defender's real-time protection opens the file
    /// exclusively to scan it. If we try to read the source during that
    /// scan window (typically &lt;300 ms but can spike to 1-2 s on slow
    /// disks / older Defender engines), <see cref="File.Open"/> throws
    /// IOException — which crashes the entire StartScript RPC.</para>
    ///
    /// <para><b>Strategy</b>: 5 attempts × exponential backoff (100 / 200 /
    /// 400 / 800 / 1000 ms). Only retries on IOException — propagates any
    /// other exception immediately. Cancellation token observed between
    /// attempts so an operator-initiated CancelScript doesn't sit through
    /// the full ~2.5 s budget.</para>
    /// </summary>
    internal static void SaveDataStreamWithRetry(ScriptFile file, string tempPath, CancellationToken cancellationToken)
        => RetryOnTransientIO(
            operation: () => file.Contents.Receiver()
                .SaveToAsync(tempPath, cancellationToken)
                .GetAwaiter().GetResult(),
            contextName: file?.Name ?? "(unnamed)",
            cancellationToken: cancellationToken);

    /// <summary>
    /// Generic retry-with-backoff for transient <see cref="IOException"/>.
    /// Extracted from <see cref="SaveDataStreamWithRetry"/> so the retry +
    /// timing + cancellation contract is tested directly with a synthetic
    /// failing operation, without involving Halibut's DataStream wire types.
    /// </summary>
    /// <param name="operation">The IO operation to run. Re-invoked on transient IOException.</param>
    /// <param name="contextName">Human-readable subject for log messages (typically the file name).</param>
    /// <param name="cancellationToken">Observed before every attempt + during inter-attempt delay.</param>
    /// <exception cref="IOException">Re-thrown wrapped after every attempt fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation requested between attempts.</exception>
    internal static void RetryOnTransientIO(Action operation, string contextName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var maxAttempts = ResolveMaxAttempts();
        var delayMs = SaveDataStreamInitialDelayMs;
        IOException lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                operation();

                if (attempt > 1)
                    Log.Warning(
                        "IO operation for '{Context}' succeeded on attempt {Attempt}/{Max} after transient file-lock IOException. " +
                        "Suspect: Windows Defender / AV holding the source temp file during real-time scan.",
                        contextName, attempt, maxAttempts);

                return;
            }
            catch (IOException ex)
            {
                // Record EVERY IOException — including the final-attempt one — so the
                // wrapper thrown after the loop carries the most-recent message as
                // InnerException. Don't use `when (attempt < maxAttempts)` here — that
                // would skip the catch on the last attempt and propagate the raw
                // IOException unwrapped, defeating the retry-count + env-var-hint
                // operator messaging.
                lastException = ex;

                if (attempt >= maxAttempts) break;    // fall through to wrap-and-throw

                Log.Debug(ex,
                    "IO operation attempt {Attempt}/{Max} for '{Context}' hit IOException; retrying after {Delay}ms",
                    attempt, maxAttempts, contextName, delayMs);

                Task.Delay(delayMs, cancellationToken).GetAwaiter().GetResult();

                delayMs = Math.Min(delayMs * 2, SaveDataStreamMaxDelayMs);
            }
        }

        // Fell through: every attempt failed. Surface the LAST IOException with
        // a richer message so operators see the retry count without having to
        // tail Tentacle logs.
        throw new IOException(
            $"IO operation for '{contextName}' failed after {maxAttempts} attempts. " +
            $"The source temp file remained locked by another process — most likely Windows Defender / AV. " +
            $"If your AV is known-aggressive, raise {SaveDataStreamMaxAttemptsEnvVar} above the default {SaveDataStreamMaxAttempts} or exclude " +
            $"the Tentacle workspace from real-time scanning. See last inner exception for the underlying message.",
            lastException);
    }

    private static int ResolveMaxAttempts()
    {
        var raw = Environment.GetEnvironmentVariable(SaveDataStreamMaxAttemptsEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return SaveDataStreamMaxAttempts;

        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : SaveDataStreamMaxAttempts;
    }

    private static void SetDirectoryPermissions(string dirPath)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(dirPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    // ========================================================================
    // Process Execution
    // ========================================================================

    /// <summary>
    /// P0-B.2 (2026-04-24 audit): name of the environment variable that carries the
    /// sensitive-variable encryption password from this process to the spawned
    /// Calamari child. Paired with the same constant on the Calamari side — drift
    /// between the two silently breaks sensitive-variable decryption. Pinned by
    /// <c>SensitiveVariablePasswordTransportTests.CalamariSensitivePasswordEnvVar_ConstantNamePinned</c>
    /// (tentacle) and <c>RunScriptCliHandlerSensitivePasswordEnvVarTests</c>
    /// (calamari).
    /// </summary>
    internal const string CalamariSensitivePasswordEnvVar = "SQUID_CALAMARI_SENSITIVE_PASSWORD";

    private static Process StartProcess(string workDir, StartScriptCommand command)
    {
        var variablesPath = Path.Combine(workDir, "variables.json");
        var sensitiveVariablesPath = Path.Combine(workDir, "sensitiveVariables.json");

        // Calamari can today only bootstrap BASH scripts — its pipeline contains
        // exactly one writer step (<see cref="Squid.Calamari.Commands.WriteBootstrappedScriptStep"/>)
        // that does <c>File.ReadAllText("script.sh")</c> + prepends a Bash
        // <c>export VAR=...</c> preamble. There is NO <c>WriteBootstrappedPowerShellScriptStep</c>
        // and the Calamari CLI is hardcoded to <c>--script=script.sh</c> at the
        // call site below — so routing PowerShell through Calamari ALWAYS crashes
        // with <c>FileNotFoundException</c> (Tentacle wrote <c>script.ps1</c>,
        // Calamari reads <c>script.sh</c>). The agent's parent process dies, the
        // server polls GetStatus, the in-memory ticket entry is gone but the
        // state file still says <c>Progress=Running</c> — orphan detection at
        // <see cref="GetStatus"/> line 497 then returns <c>Complete + UnknownResult (-1)</c>
        // which the operator sees as <c>"Unknown result (ticket or process not found)
        // (exit code -1)"</c>.
        //
        // <para><b>Why this is safe for PowerShell flows</b>: every server-side
        // PowerShell-emitting code path (e.g. <c>IISDeployScriptBuilder</c>,
        // <c>WindowsTentacleUpgradeStrategy</c>) already inlines all required
        // variables into the rendered script body via <c>$SquidParameters</c> /
        // <c>$SquidVariables</c> hashtables. Calamari's bootstrap preamble adds
        // zero value on top of that — the script is fully self-contained from
        // the moment the server emits it. Bypassing Calamari for PowerShell
        // therefore changes nothing about variable visibility; it just routes
        // through <see cref="StartViaLauncher"/> instead, which in turn picks
        // <c>PwshCoreProcessLauncher</c> or <c>WindowsPowerShellProcessLauncher</c>
        // (auto-fallback when pwsh.exe isn't installed — see PR #352).
        //
        // <para><b>Future</b>: when Calamari gains a <c>WriteBootstrappedPowerShellScriptStep</c>
        // AND the Tentacle CLI invocation switches to a per-syntax script path
        // (<c>--script=script.ps1</c> for PowerShell, <c>--script=script.sh</c>
        // for Bash), <see cref="IsCalamariCompatible"/> can be widened back to
        // include PowerShell. Pinned by <c>IsCalamariCompatible_*</c> tests so
        // a future regression surfaces immediately.
        if (File.Exists(variablesPath) && IsCalamariCompatible(command.ScriptSyntax))
        {
            // P0-B.2: password no longer on disk — pull it from the in-memory ScriptFile
            // instance. Writing the `.key` sidecar defeated at-rest encryption; passing
            // --password= via argv leaked through ps aux / /proc/<pid>/cmdline.
            var sensitivePassword = command.Files?
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f.Name), "sensitiveVariables.json", StringComparison.Ordinal))
                ?.EncryptionPassword;

            return StartCalamariProcess(workDir, variablesPath, sensitiveVariablesPath, sensitivePassword, command.ScriptSyntax, command.Arguments);
        }

        return command.ScriptSyntax switch
        {
            ScriptType.PowerShell => StartViaLauncher(workDir, ScriptType.PowerShell, command.Arguments),
            ScriptType.Python => StartPythonProcess(workDir, command.Arguments),
            ScriptType.CSharp => StartCSharpProcess(workDir, command.Arguments),
            ScriptType.FSharp => StartFSharpProcess(workDir, command.Arguments),
            // Default arm matches Bash AND any future ScriptType not in the
            // explicit list — preserves behaviour where unknown
            // syntaxes silently fell through to bash. Kept verbatim for Rule 1.
            _ => StartViaLauncher(workDir, ScriptType.Bash, command.Arguments)
        };
    }

    /// <summary>
    /// Whether the given script <paramref name="syntax"/> can be bootstrapped by
    /// the bundled <c>squid-calamari</c> CLI.
    ///
    /// <para>Today only <see cref="ScriptType.Bash"/> qualifies — Calamari's
    /// <c>RunScriptCommand</c> pipeline contains a single bootstrap step
    /// (<c>WriteBootstrappedScriptStep</c>) that reads <c>script.sh</c> and
    /// prepends a Bash <c>export VAR=...</c> preamble. There is intentionally
    /// NO <c>WriteBootstrappedPowerShellScriptStep</c>: every server-side
    /// PowerShell-emitting code path (<c>IISDeployScriptBuilder</c>,
    /// <c>WindowsTentacleUpgradeStrategy</c>, etc.) already inlines all
    /// variables into the rendered script body via <c>$SquidParameters</c> /
    /// <c>$SquidVariables</c> hashtables, so Calamari's bootstrap preamble adds
    /// zero value on top of that.</para>
    ///
    /// <para>Routing PowerShell through Calamari ALWAYS crashed with
    /// <c>FileNotFoundException</c> (Tentacle wrote <c>script.ps1</c>, Calamari
    /// read <c>script.sh</c>). Operator-visible symptom: <c>"Unknown result
    /// (ticket or process not found) (exit code -1)"</c> from the
    /// <see cref="GetStatus"/> orphan-detection path (state file said
    /// <c>Progress=Running</c> but the Calamari child had already crashed).
    /// Pinned by <c>IsCalamariCompatible_*</c> tests.</para>
    /// </summary>
    /// <summary>
    /// Opt-in env var (Rule 8). When set to a truthy value, PowerShell
    /// scripts ALSO route through Calamari (gaining the full rewriter +
    /// convention pipeline). Default OFF preserves the conservative
    /// bash-only routing — zero behaviour change for every existing agent.
    ///
    /// <para><b>Why opt-in, not default-on</b>: server-side PowerShell-
    /// emitting paths (IISDeployScriptBuilder, WindowsTentacleUpgradeStrategy)
    /// already inline variables into the script body via $SquidParameters,
    /// so Calamari's bootstrap adds nothing for them — and routing them
    /// through the extra Calamari child process changes the process tree /
    /// cancellation / output-capture path. Operators with CUSTOM PowerShell
    /// package deploys (who want SubstituteInFiles / ConfigurationTransforms /
    /// conventions applied) opt in explicitly + reversibly.</para>
    ///
    /// <para>Pinned by <c>CalamariPowerShellEnvVar_ConstantNamePinned</c>.</para>
    /// </summary>
    public const string CalamariPowerShellEnvVar = "SQUID_TENTACLE_CALAMARI_POWERSHELL";

    /// <summary>
    /// Calamari routing predicate. Bash always qualifies. PowerShell
    /// qualifies ONLY when <see cref="CalamariPowerShellEnvVar"/> is set to
    /// a truthy value (1 / true / yes / on, case-insensitive) — the PR-4
    /// PowerShell-aware bootstrap + the per-syntax <c>--script=</c> path
    /// (PR-8) are the prerequisites that make this safe; the env gate keeps
    /// it opt-in so the default agent behaviour is unchanged.
    /// </summary>
    internal static bool IsCalamariCompatible(ScriptType syntax) => syntax switch
    {
        ScriptType.Bash => true,
        ScriptType.PowerShell => IsCalamariPowerShellEnabled(),
        _ => false
    };

    /// <summary>Parse the opt-in env flag. Truthy = 1 / true / yes / on
    /// (case-insensitive). Anything else (incl. unset) = false.</summary>
    internal static bool IsCalamariPowerShellEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(CalamariPowerShellEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    /// <summary>
    /// dispatches to the platform-resolved
    /// <see cref="IProcessLauncher"/>.  this method's body
    /// was duplicated across <c>StartBashProcess</c> + <c>StartPwshProcess</c>
    /// (both bit-for-bit identical PSI shapes minus FileName + ArgumentList).
    /// Now the per-syntax PSI shape lives in
    /// <see cref="Platform.IProcessLauncher.BuildStartInfo"/>; this method
    /// owns only the lifecycle (Start, EnableRaisingEvents) which is the same
    /// across all syntaxes.
    /// </summary>
    private static Process StartViaLauncher(string workDir, ScriptType syntax, string[] arguments)
    {
        var psi = ProcessLauncherFactory.Resolve(syntax).BuildStartInfo(workDir, arguments);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    private static Process StartCalamariProcess(
        string workDir, string variablesPath, string sensitiveVariablesPath, string? sensitivePassword, ScriptType syntax, string[] arguments)
    {
        var psi = BuildCalamariProcessStartInfo(
            workDir,
            variablesPath,
            sensitiveVariablesPath,
            sensitivePassword,
            sensitiveCiphertextExists: File.Exists(sensitiveVariablesPath),
            syntax,
            arguments);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    /// <summary>
    /// Build the <see cref="ProcessStartInfo"/> for the Calamari child. Pure function
    /// (no disk reads except the caller-provided <paramref name="sensitiveCiphertextExists"/>
    /// flag) so unit tests can verify the argv / env-var contract without spawning a
    /// real process.
    /// </summary>
    internal static ProcessStartInfo BuildCalamariProcessStartInfo(
        string workDir,
        string variablesPath,
        string sensitiveVariablesPath,
        string? sensitivePassword,
        bool sensitiveCiphertextExists,
        ScriptType syntax,
        string[] arguments,
        string? tentacleDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveCalamariExecutable(tentacleDirectory ?? AppContext.BaseDirectory, File.Exists),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("run-script");
        // PR-8: per-syntax script path. Bash → script.sh, PowerShell →
        // script.ps1 — matches what WriteScriptFile already wrote to disk
        // (ScriptFileNameFor). Calamari's PR-4 ScriptSyntaxDetector picks the
        // executor from this extension. The hardcoded "script.sh" here was
        // the co-dependent reason PowerShell couldn't route through Calamari.
        psi.ArgumentList.Add($"--script={ScriptFileNameFor(syntax)}");
        psi.ArgumentList.Add($"--variables={variablesPath}");

        if (sensitiveCiphertextExists && !string.IsNullOrEmpty(sensitivePassword))
        {
            psi.ArgumentList.Add($"--sensitive={sensitiveVariablesPath}");

            // P0-B.2: password via env var, NOT argv. Env var is readable via
            // /proc/<pid>/environ (mode 0600 — process owner / root only). argv is in
            // /proc/<pid>/cmdline (typically world-readable 0444) and in ps aux.
            psi.Environment[CalamariSensitivePasswordEnvVar] = sensitivePassword;
        }

        if (arguments != null && arguments.Length > 0)
        {
            psi.ArgumentList.Add("--");
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    internal static string ResolveCalamariExecutable(string tentacleDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        var bundledFileName = OperatingSystem.IsWindows() ? "squid-calamari.exe" : "squid-calamari";
        if (!string.IsNullOrWhiteSpace(tentacleDirectory))
        {
            var bundledPath = Path.Combine(tentacleDirectory, bundledFileName);
            if (fileExists(bundledPath))
                return bundledPath;
        }

        return bundledFileName;
    }

    /// <summary>
    /// Runs <c>python3 script.py</c>. <c>python3</c> must be on PATH; the
    /// upstream <c>squid-tentacle-linux</c> base image installs Python 3 by
    /// default, but third-party / minimal Tentacle deployments may not — in
    /// that case the process exits 127 with a clear "command not found"
    /// message routed through the standard exit-code translator.
    /// </summary>
    private static Process StartPythonProcess(string workDir, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("script.py");

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        // PYTHONUNBUFFERED forces stdout/stderr to flush immediately so the
        // SequencedLogWriter sees output as the script runs, not in a final
        // burst at process exit. Otherwise <c>print()</c> calls can sit in
        // the line-buffered pipe for tens of seconds.
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    /// <summary>
    /// Runs <c>dotnet-script script.csx</c> via the global <c>dotnet-script</c>
    /// tool. The Tentacle image bundles it via <c>dotnet tool install -g
    /// dotnet-script</c>; check the <c>Dockerfile.Tentacle.Linux</c>.
    /// </summary>
    private static Process StartCSharpProcess(string workDir, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet-script",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("script.csx");

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    /// <summary>
    /// Runs <c>dotnet fsi script.fsx</c>. <c>dotnet fsi</c> ships with the
    /// .NET SDK (not the runtime), so this requires the SDK to be installed
    /// on the agent — verified at agent build time via the SDK image base
    /// in <c>Dockerfile.Tentacle.Linux</c>.
    /// </summary>
    private static Process StartFSharpProcess(string workDir, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("fsi");
        psi.ArgumentList.Add("script.fsx");

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    internal static string ShellEscape(string arg)
        => "'" + arg.Replace("'", "'\\''") + "'";

    // ========================================================================
    // Output Handling
    // ========================================================================

    private static void BeginReadOutput(Process process, RunningScript running)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) running.NoteStreamEof();
            if (e.Data == null || running.LogWriter == null) return;
            var parsed = PodLogLineParser.Parse(e.Data);
            running.LogWriter.Append(parsed.Source, parsed.Text);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) running.NoteStreamEof();
            if (e.Data == null || running.LogWriter == null) return;
            running.LogWriter.Append(ProcessOutputSource.StdErr, e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>
    /// Records the script's real exit code on disk the moment the agent observes the process
    /// exit, instead of only when the server later calls CompleteScript. Without this an agent
    /// restart between "script exited" and "server completes it" leaves the state file saying
    /// Running with a dead PID, which the orphan path reports as UnknownResult for a script
    /// that actually succeeded.
    ///
    /// <para>Runs on the runtime's existing process-exit callback (no thread per script). The
    /// post-subscribe <c>HasExited</c> check covers a process that exited before the handler was
    /// attached; <see cref="PersistNaturalExit"/> is idempotent so double delivery is harmless.</para>
    /// </summary>
    private static void HookNaturalExitPersistence(Process process, RunningScript running)
    {
        process.Exited += (_, _) => ScheduleNaturalExitPersistence(running);
        process.EnableRaisingEvents = true;

        if (process.HasExited)
            ThreadPool.QueueUserWorkItem(_ => ScheduleNaturalExitPersistence(running));
    }

    // Runs on the process-exit callback: it must neither block nor hold the Process lock, so a script whose output
    // pipe is still open is persisted from a timer continuation once EOF arrives or the drain budget runs out.
    private static void ScheduleNaturalExitPersistence(RunningScript running)
    {
        running.NoteExitObserved();

        if (running.OutputEof.Task.IsCompleted)
        {
            PersistNaturalExit(running);
            return;
        }

        Task.WhenAny(running.OutputEof.Task, Task.Delay(running.RemainingDrainBudget()))
            .ContinueWith(_ => PersistNaturalExit(running), TaskScheduler.Default);
    }

    private static void PersistNaturalExit(RunningScript running)
    {
        try
        {
            // The persisted Complete state must never be visible before the final log lines are on disk
            // (SequencedLogWriter flushes every Append), so drain first. Bounded — see AwaitOutputDrained.
            AwaitOutputDrained(running);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Skipped exit persistence for {WorkDir}: process already released", running.WorkDir);
            return;
        }

        ReleaseIsolationAfterExit(running);

        lock (running.TerminalGate)
        {
            // Exit can be delivered twice (the Exited event and the post-subscribe HasExited check): persist once, or a second
            // delivery re-creates a state the server has already consumed through another instance.
            if (running.Finalized || running.CancelRequested || running.NaturalExitPersisted) return;

            PersistCompleteState(running, running.Process.ExitCode);

            running.NaturalExitPersisted = true;
        }
    }

    /// <summary>Test-only: delivers the process-exit notification for a tracked script again, as the runtime may.</summary>
    internal void RedeliverNaturalExitForTests(string ticketId) => PersistNaturalExit(_scripts[ticketId]);

    /// <summary>
    /// Isolation protects the RUNNING process, not the bookkeeping that follows. Holding the lock until the server's
    /// CompleteScript arrives would wedge every later script on the same mutex whenever that call is lost for good
    /// (the server proceeds on the exit code it already saw via GetStatus). The handle is idempotent, so the later
    /// Complete/Cancel release is a no-op.
    /// </summary>
    private static void ReleaseIsolationAfterExit(RunningScript running) => running.IsolationHandle?.Dispose();

    private static void MarkCancelRequested(RunningScript running)
    {
        lock (running.TerminalGate)
            running.CancelRequested = true;
    }

    private static void FinalizeState(RunningScript running, int exitCode)
    {
        lock (running.TerminalGate)
        {
            running.Finalized = true;
            PersistCompleteState(running, exitCode);
        }
    }

    /// <summary>
    /// Reads log entries from disk AND derives the response's <c>NextLogSequence</c>
    /// cursor in a single place, so the cursor is always one past the highest
    /// sequence in the entries actually returned (or <c>afterSequence + 1</c> when
    /// nothing is returned).
    ///
    /// <para><b>Why this is a single-source helper</b>: the previous implementation
    /// computed the cursor from a different source than the entries — either the
    /// writer's in-memory <c>NextSequence</c> counter (live-script path in
    /// <see cref="BuildStatus"/>) or a second <c>GetHighestSequence</c> call
    /// against the same file (persisted-script path in
    /// <see cref="TryBuildStatusFromPersistedLogs"/>). Both sources can race
    /// ahead of the <c>ReadFrom</c> snapshot — between the disk read and the
    /// counter / second-read, a concurrent writer can append more lines, so the
    /// returned cursor jumps past entries that were never delivered. The next
    /// poll then asks "give me from cursor onward" and silently misses the gap,
    /// which broke <c>LogCursor_SurvivesAgentRestart_AllLinesDeliveredExactlyOnce</c>
    /// in CI.</para>
    ///
    /// <para>Pinned by <c>LocalScriptServiceCursorTests</c> — every property of
    /// the (logs, cursor) pair is verified there: empty file, mid-stream cursor,
    /// past-end cursor, two-phase reads with no gap.</para>
    /// </summary>
    internal static (List<ProcessOutput> Logs, long NextSequence) ReadLogsAndCursor(string logFilePath, long afterSequence)
    {
        var reader = new SequencedLogReader(logFilePath);
        var entries = reader.ReadFrom(afterSequence);
        var logs = entries.Select(e => e.ToProcessOutput()).ToList();

        // Cursor = max(returned sequence) + 1. If nothing was returned, cursor
        // stays at afterSequence + 1 — preserves the caller's progress without
        // either advancing past undelivered entries or rewinding.
        var nextSequence = entries.Count > 0
            ? entries[entries.Count - 1].Sequence + 1
            : afterSequence + 1;

        return (logs, nextSequence);
    }

    private static (List<ProcessOutput> Logs, long NextSequence) ReadLogsAndCursor(RunningScript running, long afterSequence)
    {
        if (running.LogWriter == null) return (new List<ProcessOutput>(), afterSequence + 1);
        return ReadLogsAndCursor(running.LogFilePath, afterSequence);
    }

    private static List<ProcessOutput> ReadLogs(RunningScript running, long afterSequence)
        => ReadLogsAndCursor(running, afterSequence).Logs;

    // ========================================================================
    // Cleanup
    // ========================================================================

    private static ScriptStatusResponse CompletedResponse(ScriptTicket ticket, int exitCode)
        => new(ticket, ProcessState.Complete, exitCode, new List<ProcessOutput>(), 0);

    private static void CleanupWorkDir(string workDir)
    {
        try
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cleanup work directory {WorkDir}", workDir);
        }
    }

    private void CleanupOrphanedWorkspacesIfDue()
    {
        if (DateTimeOffset.UtcNow - _lastCleanupTime < CleanupInterval) return;

        _lastCleanupTime = DateTimeOffset.UtcNow;
        var count = CleanupOrphanedWorkspaces(OrphanMaxAge);

        if (count > 0)
            Log.Information("Cleaned up {Count} orphaned workspace(s)", count);
    }

    internal static int CleanupOrphanedWorkspaces(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var cleaned = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "squid-tentacle-*"))
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        cleaned++;
                        // : per-deletion counter for Prometheus.
                        // Only fires when delete actually succeeded (skipped /
                        // failed dirs do NOT count).
                        Squid.Tentacle.Health.TentacleMetrics.OrphanedWorkspaceCleaned();
                    }
                    catch
                    {
                        // Best-effort cleanup
                    }
                }
            }
        }
        catch
        {
            // Enumeration failure — non-fatal
        }

        return cleaned;
    }

    // ========================================================================
    // Inner Types
    // ========================================================================

    private sealed class RunningScript
    {
        public Process Process { get; }
        public string WorkDir { get; }
        public string LogFilePath { get; }
        public IDisposable IsolationHandle { get; }
        public IScriptStateStore? StateStore { get; }
        public SequencedLogWriter? LogWriter { get; }
        public StartScriptCommand? Command { get; }
        public DateTimeOffset StartedAt { get; }
        public string? TraceId { get; }

        /// <summary>Completes when both stdout and stderr readers have reported end-of-stream.</summary>
        public readonly TaskCompletionSource OutputEof = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How long after the exit is first observed we wait for <see cref="OutputEof"/> before giving up on the pipe.</summary>
        public TimeSpan OutputDrainTimeout { get; init; } = DefaultOutputDrainTimeout;

        /// <summary>Serialises the single bounded drain (see <c>AwaitOutputDrained</c>); a per-script lock, never shared across tickets.</summary>
        public readonly object DrainGate = new();

        /// <summary>Set once the drain finished (EOF reached or output capture stopped).</summary>
        public volatile bool OutputDrained;

        private int _openStreams = 2;
        private long _exitObservedAt;

        public void NoteStreamEof()
        {
            if (Interlocked.Decrement(ref _openStreams) == 0)
                OutputEof.TrySetResult();
        }

        /// <summary>Anchors the drain deadline at the first moment the exit was observed (idempotent).</summary>
        public void NoteExitObserved() => Interlocked.CompareExchange(ref _exitObservedAt, Stopwatch.GetTimestamp(), 0);

        public TimeSpan RemainingDrainBudget()
        {
            var anchor = Interlocked.Read(ref _exitObservedAt);

            if (anchor == 0) return OutputDrainTimeout;

            var remaining = OutputDrainTimeout - Stopwatch.GetElapsedTime(anchor);

            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        /// <summary>Guards the terminal-state flags below and serialises every state-file completion write.</summary>
        public readonly object TerminalGate = new();

        /// <summary>Set (under <see cref="TerminalGate"/>) when CancelScript starts; suppresses the natural-exit write.</summary>
        public bool CancelRequested;

        /// <summary>Set (under <see cref="TerminalGate"/>) once CompleteScript/CancelScript recorded the final result.</summary>
        public bool Finalized;
        public bool NaturalExitPersisted;

        /// <summary>Set by V2 CancelScript so later status/complete calls report Canceled instead of the kill signal's exit code.</summary>
        public volatile bool V2Canceled;

        /// <summary>Set (before the kill) when a V2 cancel is being carried out; the outcome is recorded once the cancel finishes.</summary>
        public volatile bool V2CancelIssued;

        public int? V2CancelExitCode => V2Canceled ? ScriptExitCodes.Canceled : null;

        /// <summary>A cancel was issued or recorded, so a racing Complete must report Canceled rather than the kill signal's exit code.</summary>
        public bool V2CancelWins => V2CancelIssued || V2Canceled;

        public long LogSequence => LogWriter?.NextSequence ?? 0;

        public RunningScript(Process process, string workDir, IDisposable isolationHandle, IScriptStateStore? stateStore = null, SequencedLogWriter? logWriter = null, StartScriptCommand? command = null, string? traceId = null)
        {
            Process = process;
            WorkDir = workDir;
            LogFilePath = Path.Combine(workDir, "output.log");
            IsolationHandle = isolationHandle;
            StateStore = stateStore;
            LogWriter = logWriter;
            Command = command;
            StartedAt = DateTimeOffset.UtcNow;
            TraceId = traceId;
        }
    }
}
