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
    private readonly IScriptStateStoreFactory _stateStoreFactory;
    private readonly IAdmissionPolicySource? _admissionPolicySource;
    private volatile bool _draining;
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OrphanMaxAge = TimeSpan.FromHours(24);
    private static readonly string AgentVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public LocalScriptService() : this(new ScriptStateStoreFactory()) { }

    public LocalScriptService(IScriptStateStoreFactory stateStoreFactory, IAdmissionPolicySource? admissionPolicySource = null)
    {
        _stateStoreFactory = stateStoreFactory ?? throw new ArgumentNullException(nameof(stateStoreFactory));
        _admissionPolicySource = admissionPolicySource;
    }

    public ScriptStatusResponse StartScript(StartScriptCommand command)
    {
        if (_draining)
            throw new InvalidOperationException("Tentacle is shutting down and cannot accept new scripts");

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

        var isolationHandle = _isolationMutex.AcquireAsync(command).GetAwaiter().GetResult();

        if (isolationHandle == null)
            throw new InvalidOperationException("Failed to acquire script isolation mutex within the configured timeout");

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
        WriteAdditionalFiles(workDir, command.Files);

        var process = StartProcess(workDir, command);
        var logWriter = new SequencedLogWriter(Path.Combine(workDir, "output.log"));
        var traceId = ResolveParentTraceId(command);
        var running = new RunningScript(process, workDir, isolationHandle, stateStore, logWriter, command, traceId);

        BeginReadOutput(process, running);
        _scripts[ticketId] = running;

        stateStore.Save(new ScriptState
        {
            TicketId = ticketId,
            Progress = ScriptProgress.Running,
            ProcessId = process.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow
        });

        Log.Information("Started script {TicketId} in {WorkDir} (syntax: {Syntax})", ticketId, workDir, syntax);

        WaitForEarlyCompletion(running, command.DurationToWaitForScriptToFinish);

        return BuildStatus(command.ScriptTicket, running);
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

    private static string ResolveWorkDir(string ticketId)
        => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");

    private static void WaitForEarlyCompletion(RunningScript running, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        try { running.Process.WaitForExit(duration); }
        catch { /* Best-effort early return — polling will catch final state */ }
    }

    private static ScriptStatusResponse BuildStatus(ScriptTicket ticket, RunningScript running, long afterSequence = -1)
    {
        var logs = ReadLogs(running, afterSequence);
        var state = running.Process.HasExited ? ProcessState.Complete : ProcessState.Running;
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : 0;

        return new ScriptStatusResponse(ticket, state, exitCode, logs, running.LogWriter?.NextSequence ?? 0);
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

        var logReader = new SequencedLogReader(Path.Combine(workDir, "output.log"));
        var entries = logReader.ReadFrom(lastSeq - 1);
        var logs = entries.Select(e => e.ToProcessOutput()).ToList();

        var processState = state.IsComplete() ? ProcessState.Complete : ProcessState.Running;
        var exitCode = state.ExitCode ?? 0;
        var nextSeq = state.NextLogSequence > 0
            ? state.NextLogSequence
            : Math.Max(0, logReader.GetHighestSequence() + 1);

        response = new ScriptStatusResponse(ticket, processState, exitCode, logs, nextSeq);
        return true;
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
        {
            var workDir = ResolveWorkDir(command.Ticket.TaskId);
            DeletePersistedStateIfAny(workDir);
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
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
        {
            var workDir = ResolveWorkDir(command.Ticket.TaskId);
            DeletePersistedStateIfAny(workDir);
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

    internal static void WriteAdditionalFiles(string workDir, List<ScriptFile> files)
    {
        if (files == null) return;

        var resolvedWorkDir = Path.GetFullPath(workDir);

        foreach (var file in files)
        {
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
                file.Contents.Receiver()
                    .SaveToAsync(tempPath, CancellationToken.None)
                    .GetAwaiter().GetResult();

                File.Move(tempPath, filePath, overwrite: true);

                if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && !IsScriptFile(file.Name))
                    File.SetUnixFileMode(filePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

                if (file.EncryptionPassword != null)
                    File.WriteAllText(filePath + ".key", file.EncryptionPassword);
            }
            catch
            {
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

    private static Process StartProcess(string workDir, StartScriptCommand command)
    {
        var variablesPath = Path.Combine(workDir, "variables.json");
        var sensitiveVariablesPath = Path.Combine(workDir, "sensitiveVariables.json");
        var sensitiveKeyPath = sensitiveVariablesPath + ".key";

        // Calamari only knows how to bootstrap Bash and PowerShell scripts (it
        // generates `export VAR=...` / `$VAR=...` preambles). For Python /
        // CSharp / FSharp we bypass Calamari and exec the interpreter directly
        // — variable injection still happens through process env vars set by
        // the caller (or the script can read variables.json itself).
        var canUseCalamari = command.ScriptSyntax == ScriptType.Bash || command.ScriptSyntax == ScriptType.PowerShell;

        if (File.Exists(variablesPath) && canUseCalamari)
            return StartCalamariProcess(workDir, variablesPath, sensitiveVariablesPath, sensitiveKeyPath, command.Arguments);

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
        string workDir, string variablesPath, string sensitiveVariablesPath, string sensitiveKeyPath, string[] arguments)
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
        psi.ArgumentList.Add($"--script=script.sh");
        psi.ArgumentList.Add($"--variables={variablesPath}");

        if (File.Exists(sensitiveVariablesPath) && File.Exists(sensitiveKeyPath))
        {
            var password = File.ReadAllText(sensitiveKeyPath).Trim();
            psi.ArgumentList.Add($"--sensitive={sensitiveVariablesPath}");
            psi.ArgumentList.Add($"--password={password}");
        }

        if (arguments != null && arguments.Length > 0)
        {
            psi.ArgumentList.Add("--");
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
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

    private static List<ProcessOutput> ReadLogs(RunningScript running, long afterSequence)
    {
        if (running.LogWriter == null) return new List<ProcessOutput>();

        var reader = new SequencedLogReader(running.LogFilePath);
        var entries = reader.ReadFrom(afterSequence);
        return entries.Select(e => e.ToProcessOutput()).ToList();
    }

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
