using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Serilog;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Observability;
using Squid.Tentacle.ScriptExecution.Logging;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.Security.Admission;

namespace Squid.Tentacle.ScriptExecution;

public class LocalScriptService : IScriptService, ITentacleScriptBackend, IGracefulShutdownAware, IRunningScriptReporter
{
    public bool IsRunningScript(string ticketId)
        => !string.IsNullOrEmpty(ticketId) && _scripts.ContainsKey(ticketId);


    private readonly ConcurrentDictionary<string, RunningScript> _scripts = new();
    private readonly ScriptIsolationMutex _isolationMutex = new();
    /// <summary>
    /// P1-Phase11 (audit ARCH.9 Plan A) — per-ticket soft-cancellation registry.
    /// Bridges the wire-level "no CT" limitation with internal async work
    /// (file save, mutex acquire) so a CancelScript RPC can actually
    /// short-circuit in-flight operations rather than letting them run to
    /// completion. <see cref="StartScript"/> calls
    /// <see cref="ScriptCancellationRegistry.GetOrCreate"/>;
    /// <see cref="CancelScript"/> calls <see cref="ScriptCancellationRegistry.Cancel"/>;
    /// <see cref="CompleteScript"/> calls <see cref="ScriptCancellationRegistry.Cleanup"/>.
    /// </summary>
    private readonly ScriptCancellationRegistry _cancellationRegistry = new();

    /// <summary>Test-only — observe the per-ticket CTS registry's live entry count.
    /// Used to verify Cleanup is called on every exit path of LocalScriptService.</summary>
    internal int CancellationRegistryCountForTests => _cancellationRegistry.CountForTests;
    private readonly IScriptStateStoreFactory _stateStoreFactory;
    private readonly IAdmissionPolicySource? _admissionPolicySource;
    private volatile bool _draining;
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// P1-Phase9.11 — env-var override for the orphan-workspace TTL.
    /// Default 24h was hardcoded pre-Phase-9.11; operators with high deploy
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

    public LocalScriptService() : this(new ScriptStateStoreFactory()) { }

    public LocalScriptService(IScriptStateStoreFactory stateStoreFactory, IAdmissionPolicySource? admissionPolicySource = null)
    {
        _stateStoreFactory = stateStoreFactory ?? throw new ArgumentNullException(nameof(stateStoreFactory));
        _admissionPolicySource = admissionPolicySource;
    }

    public ScriptStatusResponse StartScript(StartScriptCommand command)
    {
        // P1-T.11 (Phase-5 follow-up to 2026-04-24 audit): the pre-fix path
        // threw InvalidOperationException, which Halibut wrapped as a generic
        // RPC failure → server logged a cryptic message and marked the
        // deployment failed. The new contract: return a structured Complete
        // response with the AgentDraining sentinel exit code. Halibut returns
        // it normally, the operator gets an actionable error in stderr, and
        // a future server release can recognise the exit code as retry-able.
        if (_draining) return BuildDrainRejection(command);

        if (command.ScriptTicket == null)
            throw new ArgumentException("StartScriptCommand.ScriptTicket is required for idempotent execution", nameof(command));

        var ticketId = command.ScriptTicket.TaskId;

        // Admission policy is the agent-side last-line-of-defence. It runs before
        // any idempotency lookup so even a redelivered StartScript is re-evaluated
        // against the current policy (the rules may have tightened since the
        // original submission). Denial returns -403 and never touches the workspace.
        if (TryEvaluateAdmission(command, out var denial))
            return denial!;

        if (TryReturnInMemoryStatus(command.ScriptTicket, out var inMemoryResponse))
            return inMemoryResponse!;

        var workDir = ResolveWorkDir(ticketId);

        if (TryReturnPersistedStatus(command.ScriptTicket, workDir, out var persistedResponse))
            return persistedResponse!;

        DiskSpaceChecker.EnsureDiskHasEnoughFreeSpace(Path.GetTempPath());
        CleanupOrphanedWorkspacesIfDue();

        // P1-Phase11.2 (audit ARCH.9 F1.1) — pure-sync mutex acquire.
        // Pre-Phase-11.2 this was AcquireAsync(...).GetAwaiter().GetResult():
        // sync-over-async pattern that burned a Halibut RPC thread on a
        // Task.Delay-based async polling loop (allocation churn, threadpool
        // pressure under contention). The new TryAcquireBlocking uses
        // synchronous Thread.Sleep / WaitHandle.WaitOne polling — same
        // observable behaviour, no Task allocation, no async state machine.
        //
        // The CancellationToken plumbed in is the per-ticket soft-cancel
        // token from the registry — a CancelScript RPC arriving mid-acquire
        // short-circuits the polling loop instead of waiting for the full
        // configured timeout.
        var softCancelToken = _cancellationRegistry.GetOrCreate(command.ScriptTicket);
        var isolationHandle = _isolationMutex.TryAcquireBlocking(command, softCancelToken);

        if (isolationHandle == null)
            throw new InvalidOperationException("Failed to acquire script isolation mutex within the configured timeout");

        // P0-T.5 (2026-04-24 audit): ownership of isolationHandle stays with the local
        // until RunningScript is constructed. Every step between acquire and that
        // construction can throw (Directory.CreateDirectory, state-store save, file
        // writes, process start). Pre-fix, any such exception leaked the handle and
        // the mutex stayed held forever — subsequent FullIsolation scripts with the
        // same mutex name blocked until their configured timeout fired. The T.7 fix
        // made this more reachable because WriteAdditionalFiles now rethrows.
        //
        // The try / catch transfers disposal responsibility in one place: if we get
        // past RunningScript construction, ownership is RunningScript's (it disposes
        // the handle on Complete / Cancel / error). We null out the local so the
        // catch block doesn't double-dispose.
        try
        {
            Directory.CreateDirectory(workDir);
            SetDirectoryPermissions(workDir);

            var stateStore = _stateStoreFactory.Create(workDir);
            stateStore.Save(new ScriptState
            {
                TicketId = ticketId,
                Progress = ScriptProgress.Starting,
                CreatedAt = DateTimeOffset.UtcNow
            });

            var syntax = command.ScriptSyntax;
            WriteScriptFile(workDir, command.ScriptBody, syntax);
            // P1-Phase11.3: thread the per-ticket soft-cancel token so a
            // CancelScript RPC arriving mid-write actually aborts.
            WriteAdditionalFiles(workDir, command.Files, softCancelToken);

            var process = StartProcess(workDir, command);
            var logWriter = new SequencedLogWriter(Path.Combine(workDir, "output.log"));
            var traceId = ResolveParentTraceId(command);
            var running = new RunningScript(process, workDir, isolationHandle, stateStore, logWriter, command, traceId);

            // Ownership transferred — RunningScript.Dispose now owns the mutex handle.
            isolationHandle = null;

            BeginReadOutput(process, running);
            _scripts[ticketId] = running;

            stateStore.Save(new ScriptState
            {
                TicketId = ticketId,
                Progress = ScriptProgress.Running,
                ProcessId = process.Id,
                // OS-reported start time captured alongside the PID so orphan-state
                // detection (TryBuildStatusFromPersistedLogs) can distinguish
                // "same process still running" from "PID got recycled to an
                // unrelated process". Without this, a long-running tentacle
                // eventually hits PID wrap and reports a recycled PID's live
                // status as the original script's — reintroducing the
                // observer-hang bug's latent sequel.
                ProcessStartedAt = TryGetProcessStartTime(process),
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow
            });

            Log.Information("Started script {TicketId} in {WorkDir} (syntax: {Syntax})", ticketId, workDir, syntax);

            WaitForEarlyCompletion(running, command.DurationToWaitForScriptToFinish);

            return BuildStatus(command.ScriptTicket, running);
        }
        catch
        {
            // Dispose releases the mutex; LockRelease.Dispose is idempotent so re-entry
            // during exception unwinding is safe. Null after the successful RunningScript
            // transfer means this is a no-op on the happy path.
            isolationHandle?.Dispose();
            throw;
        }
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

    private bool TryReturnInMemoryStatus(ScriptTicket ticket, out ScriptStatusResponse? response)
    {
        response = null;
        if (!_scripts.TryGetValue(ticket.TaskId, out var existing)) return false;
        response = BuildStatus(ticket, existing);
        return true;
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

    private static ScriptStatusResponse BuildStatus(ScriptTicket ticket, RunningScript running, long afterSequence = -1)
    {
        // Cursor MUST be derived from the same disk read as `logs` — see
        // ReadLogsAndCursor's XML for the race that the previous two-source
        // code (ReadLogs + LogWriter.NextSequence) opened.
        var (logs, nextSequence) = ReadLogsAndCursor(running, afterSequence);
        var state = running.Process.HasExited ? ProcessState.Complete : ProcessState.Running;
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : 0;

        return new ScriptStatusResponse(ticket, state, exitCode, logs, nextSequence);
    }

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        if (_scripts.TryGetValue(request.Ticket.TaskId, out var running))
            return BuildStatus(request.Ticket, running, request.LastLogSequence - 1);

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
        // P1-Phase11 audit follow-up (#2): null-ticket NRE defence. Pre-fix
        // a malformed RPC with null Ticket caused NullReferenceException
        // inside the registry's TaskId access — Halibut wrapped as opaque
        // RPC failure. Fail explicitly so the operator gets a structured
        // error message naming the actual problem.
        if (command.Ticket == null)
            throw new ArgumentException("CompleteScriptCommand.Ticket is required", nameof(command));

        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
        {
            var workDir = ResolveWorkDir(command.Ticket.TaskId);
            DeletePersistedStateIfAny(workDir);
            // P1-Phase11 audit follow-up (#1): CompleteScript early-return
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
            return CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult);
        }

        if (!running.Process.HasExited)
            running.Process.WaitForExit(TimeSpan.FromSeconds(30));

        var logs = ReadLogs(running, command.LastLogSequence - 1);
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : ScriptExitCodes.Timeout;

        PersistCompleteState(running, exitCode);

        // Write the execution manifest BEFORE workspace cleanup so the audit
        // artifact is guaranteed to hit disk even if cleanup aborts. The manifest
        // is useful to operators doing post-mortems even after the workspace
        // itself is gone — it gets copied out to a durable archive externally.
        WriteExecutionManifest(running, command.Ticket.TaskId, exitCode);

        running.LogWriter?.Dispose();
        running.IsolationHandle?.Dispose();
        CleanupWorkDir(running.WorkDir);
        running.Process.Dispose();

        // P1-Phase11.2: release the per-ticket soft-cancel CTS now the
        // script has reached a terminal state. Without this, the registry
        // accumulates one CTS per script ever run — small leak but
        // unbounded over agent lifetime.
        _cancellationRegistry.Cleanup(command.Ticket);

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, exitCode, logs, running.LogSequence);
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
        // P1-Phase11 audit follow-up (#2): null-ticket NRE defence. Pre-fix
        // a malformed RPC with null Ticket caused NullReferenceException
        // inside the registry's TaskId access. Match StartScript's pattern.
        if (command.Ticket == null)
            throw new ArgumentException("CancelScriptCommand.Ticket is required", nameof(command));

        // P1-Phase11.2 (audit ARCH.9 F2.x soft-cancel): flip the registry's
        // CTS BEFORE attempting to remove from _scripts. This signals any
        // in-flight async work (mutex acquire, file save) that's NOT yet
        // tracked in _scripts — a CancelScript that races a slow StartScript
        // can short-circuit it via the soft-cancel token. The early-cancel
        // sentinel pattern (see ScriptCancellationRegistry.Cancel) handles
        // the out-of-order case where Cancel arrives BEFORE the matching
        // GetOrCreate.
        _cancellationRegistry.Cancel(command.Ticket);

        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
        {
            var workDir = ResolveWorkDir(command.Ticket.TaskId);
            DeletePersistedStateIfAny(workDir);
            _cancellationRegistry.Cleanup(command.Ticket);
            return CompletedResponse(command.Ticket, ScriptExitCodes.Canceled);
        }

        try
        {
            if (!running.Process.HasExited)
                running.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill process for ticket {TicketId}", command.Ticket.TaskId);
        }

        var logs = ReadLogs(running, command.LastLogSequence - 1);

        PersistCompleteState(running, ScriptExitCodes.Canceled);

        running.LogWriter?.Dispose();
        running.IsolationHandle?.Dispose();
        CleanupWorkDir(running.WorkDir);
        running.Process.Dispose();

        // P1-Phase11.2: dispose the per-ticket CTS now the script has been
        // cancelled. Idempotent — safe even if Cancel was a no-op above.
        _cancellationRegistry.Cleanup(command.Ticket);

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled, logs, running.LogSequence);
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

    public async Task WaitForDrainAsync(TimeSpan timeout)
    {
        _draining = true;
        Log.Information("LocalScriptService drain started. {Count} script(s) active", _scripts.Count);

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            while (!_scripts.IsEmpty && !cts.Token.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout — force-kill remaining
        }

        foreach (var (ticketId, running) in _scripts)
        {
            try
            {
                if (!running.Process.HasExited)
                {
                    Log.Warning("Drain timeout — killing script {TicketId}", ticketId);
                    running.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to kill script {TicketId} during drain", ticketId);
            }
        }

        Log.Information("LocalScriptService drain complete");
    }

    // ========================================================================
    // Script File Writing
    // ========================================================================

    internal static void WriteScriptFile(string workDir, string scriptBody, ScriptType syntax)
    {
        var scriptPath = Path.Combine(workDir, ScriptFileNameFor(syntax));

        File.WriteAllText(scriptPath, scriptBody, EncodingFor(syntax));

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

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
            // P1-Phase11.3 (audit ARCH.9 F1.2): observe soft-cancel BETWEEN
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
                // P1-Phase11.3 (audit ARCH.9 F1.2): pass the per-ticket
                // soft-cancel token through to the DataStream receiver.
                // Pre-Phase-11.3 this was hardcoded CancellationToken.None
                // — a 1GB sensitiveVariables.json payload would write to
                // completion regardless of CancelScript. Now mid-stream
                // cancel aborts the SaveToAsync via the underlying
                // CopyToAsync's CT plumbing.
                file.Contents.Receiver()
                    .SaveToAsync(tempPath, cancellationToken)
                    .GetAwaiter().GetResult();

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

        // Calamari only knows how to bootstrap Bash and PowerShell scripts (it
        // generates `export VAR=...` / `$VAR=...` preambles). For Python /
        // CSharp / FSharp we bypass Calamari and exec the interpreter directly
        // — variable injection still happens through process env vars set by
        // the caller (or the script can read variables.json itself).
        var canUseCalamari = command.ScriptSyntax == ScriptType.Bash || command.ScriptSyntax == ScriptType.PowerShell;

        if (File.Exists(variablesPath) && canUseCalamari)
        {
            // P0-B.2: password no longer on disk — pull it from the in-memory ScriptFile
            // instance. Writing the `.key` sidecar defeated at-rest encryption; passing
            // --password= via argv leaked through ps aux / /proc/<pid>/cmdline.
            var sensitivePassword = command.Files?
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f.Name), "sensitiveVariables.json", StringComparison.Ordinal))
                ?.EncryptionPassword;

            return StartCalamariProcess(workDir, variablesPath, sensitiveVariablesPath, sensitivePassword, command.Arguments);
        }

        return command.ScriptSyntax switch
        {
            ScriptType.PowerShell => StartPwshProcess(workDir, command.Arguments),
            ScriptType.Python => StartPythonProcess(workDir, command.Arguments),
            ScriptType.CSharp => StartCSharpProcess(workDir, command.Arguments),
            ScriptType.FSharp => StartFSharpProcess(workDir, command.Arguments),
            _ => StartBashProcess(workDir, command.Arguments)
        };
    }

    private static Process StartCalamariProcess(
        string workDir, string variablesPath, string sensitiveVariablesPath, string? sensitivePassword, string[] arguments)
    {
        var psi = BuildCalamariProcessStartInfo(
            workDir,
            variablesPath,
            sensitiveVariablesPath,
            sensitivePassword,
            sensitiveCiphertextExists: File.Exists(sensitiveVariablesPath),
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
        string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "squid-calamari",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("run-script");
        psi.ArgumentList.Add("--script=script.sh");
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

    private static Process StartBashProcess(string workDir, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("script.sh");

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    private static Process StartPwshProcess(string workDir, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Force UTF-8 so non-ASCII output (Chinese, emoji, etc.) round-trips
            // correctly instead of going through the Windows OEM codepage.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add("script.ps1");

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
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
            if (e.Data == null || running.LogWriter == null) return;
            var parsed = PodLogLineParser.Parse(e.Data);
            running.LogWriter.Append(parsed.Source, parsed.Text);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null || running.LogWriter == null) return;
            running.LogWriter.Append(ProcessOutputSource.StdErr, e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
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
                        // P1-Phase9.11: per-deletion counter for Prometheus.
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
