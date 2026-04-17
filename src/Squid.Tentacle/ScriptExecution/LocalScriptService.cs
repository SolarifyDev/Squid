using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Serilog;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.ScriptExecution;

public class LocalScriptService : IScriptService, ITentacleScriptBackend, IGracefulShutdownAware
{
    private readonly ConcurrentDictionary<string, RunningScript> _scripts = new();
    private readonly ScriptIsolationMutex _isolationMutex = new();
    private volatile bool _draining;
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OrphanMaxAge = TimeSpan.FromHours(24);

    public ScriptStatusResponse StartScript(StartScriptCommand command)
    {
        if (_draining)
            throw new InvalidOperationException("Tentacle is shutting down and cannot accept new scripts");

        if (command.ScriptTicket == null)
            throw new ArgumentException("StartScriptCommand.ScriptTicket is required for idempotent execution", nameof(command));

        var ticketId = command.ScriptTicket.TaskId;

        if (_scripts.TryGetValue(ticketId, out var existing))
            return BuildStatus(command.ScriptTicket, existing);

        DiskSpaceChecker.EnsureDiskHasEnoughFreeSpace(Path.GetTempPath());
        CleanupOrphanedWorkspacesIfDue();

        var isolationHandle = _isolationMutex.AcquireAsync(command).GetAwaiter().GetResult();

        if (isolationHandle == null)
            throw new InvalidOperationException("Failed to acquire script isolation mutex within the configured timeout");

        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
        Directory.CreateDirectory(workDir);
        SetDirectoryPermissions(workDir);

        var syntax = command.ScriptSyntax;
        WriteScriptFile(workDir, command.ScriptBody, syntax);
        WriteAdditionalFiles(workDir, command.Files);

        var process = StartProcess(workDir, command);
        var running = new RunningScript(process, workDir, isolationHandle);

        BeginReadOutput(process, running);
        _scripts[ticketId] = running;

        Log.Information("Started script {TicketId} in {WorkDir} (syntax: {Syntax})", ticketId, workDir, syntax);

        WaitForEarlyCompletion(running, command.DurationToWaitForScriptToFinish);

        return BuildStatus(command.ScriptTicket, running);
    }

    private static void WaitForEarlyCompletion(RunningScript running, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        try { running.Process.WaitForExit(duration); }
        catch { /* Best-effort early return — polling will catch final state */ }
    }

    private static ScriptStatusResponse BuildStatus(ScriptTicket ticket, RunningScript running)
    {
        var logs = DrainLogs(running);
        var state = running.Process.HasExited ? ProcessState.Complete : ProcessState.Running;
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : 0;

        return new ScriptStatusResponse(ticket, state, exitCode, logs, running.LogSequence);
    }

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        if (!_scripts.TryGetValue(request.Ticket.TaskId, out var running))
            return CompletedResponse(request.Ticket, ScriptExitCodes.UnknownResult);

        var logs = DrainLogs(running);
        var state = running.Process.HasExited ? ProcessState.Complete : ProcessState.Running;
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : 0;

        return new ScriptStatusResponse(request.Ticket, state, exitCode, logs, running.LogSequence);
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
            return CompletedResponse(command.Ticket, ScriptExitCodes.UnknownResult);

        if (!running.Process.HasExited)
            running.Process.WaitForExit(TimeSpan.FromSeconds(30));

        var logs = DrainLogs(running);
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : ScriptExitCodes.Timeout;

        running.IsolationHandle?.Dispose();
        CleanupWorkDir(running.WorkDir);
        running.Process.Dispose();

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, exitCode, logs, running.LogSequence);
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command)
    {
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
            return CompletedResponse(command.Ticket, ScriptExitCodes.Canceled);

        try
        {
            if (!running.Process.HasExited)
                running.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill process for ticket {TicketId}", command.Ticket.TaskId);
        }

        var logs = DrainLogs(running);

        running.IsolationHandle?.Dispose();
        CleanupWorkDir(running.WorkDir);
        running.Process.Dispose();

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled, logs, running.LogSequence);
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
        var extension = syntax == ScriptType.PowerShell ? ".ps1" : ".sh";
        var scriptPath = Path.Combine(workDir, $"script{extension}");
        File.WriteAllText(scriptPath, scriptBody);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

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

        if (File.Exists(variablesPath))
            return StartCalamariProcess(workDir, variablesPath, sensitiveVariablesPath, sensitiveKeyPath, command.Arguments);

        return command.ScriptSyntax == ScriptType.PowerShell
            ? StartPwshProcess(workDir, command.Arguments)
            : StartBashProcess(workDir, command.Arguments);
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

    internal static string ShellEscape(string arg)
        => "'" + arg.Replace("'", "'\\''") + "'";

    // ========================================================================
    // Output Handling
    // ========================================================================

    private static void BeginReadOutput(Process process, RunningScript running)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                var parsed = PodLogLineParser.Parse(e.Data);
                var output = new ProcessOutput(parsed.Source, parsed.Text);
                running.OutputQueue.Enqueue(output);
                AppendToLogFile(running.LogFilePath, output);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                var output = new ProcessOutput(ProcessOutputSource.StdErr, e.Data);
                running.OutputQueue.Enqueue(output);
                AppendToLogFile(running.LogFilePath, output);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void AppendToLogFile(string logFilePath, ProcessOutput output)
    {
        try
        {
            var line = $"[{output.Source}] {output.Text}";
            File.AppendAllText(logFilePath, line + Environment.NewLine);
        }
        catch
        {
            // Best-effort log persistence
        }
    }

    private static List<ProcessOutput> DrainLogs(RunningScript running)
    {
        var logs = new List<ProcessOutput>();

        while (running.OutputQueue.TryDequeue(out var output))
        {
            logs.Add(output);
            running.LogSequence++;
        }

        return logs;
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
        public ConcurrentQueue<ProcessOutput> OutputQueue { get; } = new();
        public long LogSequence { get; set; }

        public RunningScript(Process process, string workDir, IDisposable isolationHandle)
        {
            Process = process;
            WorkDir = workDir;
            LogFilePath = Path.Combine(workDir, "output.log");
            IsolationHandle = isolationHandle;
        }
    }
}
