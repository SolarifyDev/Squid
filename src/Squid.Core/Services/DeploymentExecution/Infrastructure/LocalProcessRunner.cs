using System.Diagnostics;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class LocalProcessRunner : ILocalProcessRunner
{
    public async Task<ScriptExecutionResult> RunAsync(
        string executable, string arguments, string workDir, CancellationToken ct, TimeSpan? timeout = null, SensitiveValueMasker masker = null)
    {
        var logLines = new List<string>();
        var stderrLines = new List<string>();
        var logLock = new object();

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            lock (logLock) { logLines.Add(e.Data); }
            Log.Information("[LocalExec:stdout] {Line}", masker?.Mask(e.Data) ?? e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            lock (logLock)
            {
                logLines.Add(e.Data);
                stderrLines.Add(e.Data);
            }

            Log.Warning("[LocalExec:stderr] {Line}", masker?.Mask(e.Data) ?? e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linkedCts = timeoutCts != null ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token) : null;
        var effectiveCt = linkedCts?.Token ?? ct;

        try
        {
            await process.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
            process.WaitForExit(); // Flush async output/error readers
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            TryKillProcess(process);
            Log.Warning("Local process timed out after {Timeout}", timeout!.Value);

            return new ScriptExecutionResult
            {
                Success = false,
                LogLines = logLines,
                StderrLines = stderrLines,
                ExitCode = ScriptExitCodes.Timeout
            };
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var exitCode = process.ExitCode;

        Log.Information("Local process exited with code {ExitCode}", exitCode);

        return new ScriptExecutionResult
        {
            Success = exitCode == 0,
            LogLines = logLines,
            StderrLines = stderrLines,
            ExitCode = exitCode
        };
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (process.HasExited) return;

            process.Kill(entireProcessTree: false);

            if (!process.WaitForExit(5000))
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill local process");
        }
    }
}
