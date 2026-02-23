using System.Collections.Concurrent;
using System.Diagnostics;
using Squid.Message.Contracts.Tentacle;
using Serilog;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.ScriptExecution;

public class LocalScriptService : IScriptService, ITentacleScriptBackend
{
    private readonly ConcurrentDictionary<string, RunningScript> _scripts = new();

    public ScriptTicket StartScript(StartScriptCommand command)
    {
        var ticketId = Guid.NewGuid().ToString("N");
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
        Directory.CreateDirectory(workDir);

        WriteScriptFile(workDir, command.ScriptBody);
        WriteAdditionalFiles(workDir, command.Files);

        var process = StartProcess(workDir);
        var running = new RunningScript(process, workDir);

        BeginReadOutput(process, running);
        _scripts[ticketId] = running;

        Log.Information("Started script {TicketId} in {WorkDir}", ticketId, workDir);

        return new ScriptTicket(ticketId);
    }

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        if (!_scripts.TryGetValue(request.Ticket.TaskId, out var running))
            return CompletedResponse(request.Ticket, -1);

        var logs = DrainLogs(running);
        var state = running.Process.HasExited ? ProcessState.Complete : ProcessState.Running;
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : 0;

        return new ScriptStatusResponse(request.Ticket, state, exitCode, logs, running.LogSequence);
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
            return CompletedResponse(command.Ticket, -1);

        if (!running.Process.HasExited)
            running.Process.WaitForExit(TimeSpan.FromSeconds(30));

        var logs = DrainLogs(running);
        var exitCode = running.Process.HasExited ? running.Process.ExitCode : -1;

        CleanupWorkDir(running.WorkDir);
        running.Process.Dispose();

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, exitCode, logs, running.LogSequence);
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command)
    {
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
            return CompletedResponse(command.Ticket, -1);

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

        CleanupWorkDir(running.WorkDir);
        running.Process.Dispose();

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, -1, logs, running.LogSequence);
    }

    private static ScriptStatusResponse CompletedResponse(ScriptTicket ticket, int exitCode)
        => new(ticket, ProcessState.Complete, exitCode, new List<ProcessOutput>(), 0);

    private static void WriteScriptFile(string workDir, string scriptBody)
    {
        var scriptPath = Path.Combine(workDir, "script.sh");
        File.WriteAllText(scriptPath, scriptBody);
    }

    private static void WriteAdditionalFiles(string workDir, List<ScriptFile> files)
    {
        if (files == null) return;

        foreach (var file in files)
        {
            var filePath = Path.Combine(workDir, file.Name);
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

    private static Process StartProcess(string workDir)
    {
        var variablesPath = Path.Combine(workDir, "variables.json");
        var sensitiveVariablesPath = Path.Combine(workDir, "sensitiveVariables.json");
        var sensitiveKeyPath = sensitiveVariablesPath + ".key";

        if (File.Exists(variablesPath))
            return StartCalamariProcess(workDir, variablesPath, sensitiveVariablesPath, sensitiveKeyPath);

        return StartBashProcess(workDir);
    }

    private static Process StartCalamariProcess(
        string workDir, string variablesPath, string sensitiveVariablesPath, string sensitiveKeyPath)
    {
        var args = $"run-script --script=script.sh --variables={variablesPath}";

        if (File.Exists(sensitiveVariablesPath) && File.Exists(sensitiveKeyPath))
        {
            var password = File.ReadAllText(sensitiveKeyPath).Trim();
            args += $" --sensitive={sensitiveVariablesPath} --password={password}";
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "squid-calamari",
                Arguments = args,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.Start();

        return process;
    }

    private static Process StartBashProcess(string workDir)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = "script.sh",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.Start();

        return process;
    }

    private static void BeginReadOutput(Process process, RunningScript running)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                running.OutputQueue.Enqueue(new ProcessOutput(ProcessOutputSource.StdOut, e.Data));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                running.OutputQueue.Enqueue(new ProcessOutput(ProcessOutputSource.StdErr, e.Data));
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
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

    private sealed class RunningScript
    {
        public Process Process { get; }
        public string WorkDir { get; }
        public ConcurrentQueue<ProcessOutput> OutputQueue { get; } = new();
        public long LogSequence { get; set; }

        public RunningScript(Process process, string workDir)
        {
            Process = process;
            WorkDir = workDir;
        }
    }
}
