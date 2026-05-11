using System.Collections.Concurrent;
using System.Diagnostics;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;

namespace Squid.E2ETests.Infrastructure;

public partial class TentacleStub
{
    private sealed class ScriptRunner : IScriptService
    {
        private readonly string _kubeconfigPath;
        private readonly ConcurrentDictionary<string, RunningScript> _scripts = new();

        public ScriptRunner(string kubeconfigPath)
        {
            _kubeconfigPath = kubeconfigPath;
        }

        public ScriptStatusResponse StartScript(StartScriptCommand command)
        {
            if (command.ScriptTicket == null)
                throw new ArgumentException("StartScriptCommand.ScriptTicket is required", nameof(command));

            var ticketId = command.ScriptTicket.TaskId;

            if (_scripts.ContainsKey(ticketId))
                return new ScriptStatusResponse(command.ScriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0);

            var workDir = Path.Combine(Path.GetTempPath(), $"tentacle-stub-{ticketId}");
            Directory.CreateDirectory(workDir);

            WriteScriptFile(workDir, command.ScriptBody);
            WriteAdditionalFiles(workDir, command.Files);

            var process = StartBashProcess(workDir);
            var running = new RunningScript(process, workDir);

            BeginReadOutput(process, running);
            _scripts[ticketId] = running;

            return new ScriptStatusResponse(command.ScriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0);
        }

        public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
        {
            if (!_scripts.TryGetValue(request.Ticket.TaskId, out var running))
            {
                return new ScriptStatusResponse(
                    request.Ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult, new List<ProcessOutput>(), 0);
            }

            var logs = DrainLogs(running, request.LastLogSequence);
            var state = running.Process.HasExited ? ProcessState.Complete : ProcessState.Running;
            var exitCode = running.Process.HasExited ? running.Process.ExitCode : 0;

            return new ScriptStatusResponse(
                request.Ticket, state, exitCode, logs, running.LogSequence);
        }

        public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
        {
            if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
            {
                return new ScriptStatusResponse(
                    command.Ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult, new List<ProcessOutput>(), 0);
            }

            if (!running.Process.HasExited)
                running.Process.WaitForExit(TimeSpan.FromSeconds(30));

            // After WaitForExit(TimeSpan) returns, the process is exited but
            // pending OutputDataReceived / ErrorDataReceived events may not have
            // dispatched yet. WaitForExit() with no arguments blocks until BOTH
            // the process has exited AND all output-redirect events have fired,
            // so the next DrainLogs sees every echo. Without this, fast scripts
            // (e.g. `echo 'hello' >&2; exit 0`) report Complete to the polling
            // observer before any stdout/stderr events reach OutputQueue → logs
            // are silently dropped → LogSink.ContainsMessage(...) fails.
            running.Process.WaitForExit();

            var logs = DrainLogs(running, command.LastLogSequence);
            var exitCode = running.Process.HasExited ? running.Process.ExitCode : ScriptExitCodes.Timeout;

            CleanupWorkDir(running.WorkDir);
            running.Process.Dispose();

            return new ScriptStatusResponse(
                command.Ticket, ProcessState.Complete, exitCode, logs, running.LogSequence);
        }

        public ScriptStatusResponse CancelScript(CancelScriptCommand command)
        {
            if (!_scripts.TryRemove(command.Ticket.TaskId, out var running))
            {
                return new ScriptStatusResponse(
                    command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0);
            }

            try
            {
                if (!running.Process.HasExited)
                    running.Process.Kill(entireProcessTree: true);
            }
            catch { }

            var logs = DrainLogs(running, command.LastLogSequence);

            CleanupWorkDir(running.WorkDir);
            running.Process.Dispose();

            return new ScriptStatusResponse(
                command.Ticket, ProcessState.Complete, ScriptExitCodes.Canceled, logs, running.LogSequence);
        }

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
                }
                catch
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }
        }

        private Process StartBashProcess(string workDir)
        {
            var homeBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin");

            var pathValue = string.Join(":",
                homeBin,
                "/usr/local/bin",
                "/opt/homebrew/bin",
                "/usr/bin",
                "/bin",
                "/usr/sbin",
                "/sbin");

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

            // Only set KUBECONFIG when a kubeconfig was supplied — Listening / Polling
            // E2E for non-K8s scripts (echo, exit) doesn't have one. Setting null here
            // throws ArgumentNullException, which prevents the script from running and
            // leaves the LogSink empty — surfacing as "ContainsMessage was false".
            if (!string.IsNullOrEmpty(_kubeconfigPath))
                process.StartInfo.Environment["KUBECONFIG"] = _kubeconfigPath;
            process.StartInfo.Environment["PATH"] = pathValue;
            process.StartInfo.Environment["HOME"] =
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            process.Start();

            return process;
        }

        private static void BeginReadOutput(Process process, RunningScript running)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    running.OutputQueue.Enqueue(
                        new ProcessOutput(ProcessOutputSource.StdOut, e.Data));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    running.OutputQueue.Enqueue(
                        new ProcessOutput(ProcessOutputSource.StdErr, e.Data));
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        private static List<ProcessOutput> DrainLogs(RunningScript running, long lastLogSequence)
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
            catch { }
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
}
