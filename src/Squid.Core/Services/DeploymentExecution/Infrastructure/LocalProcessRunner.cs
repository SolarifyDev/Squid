using System.Diagnostics;
using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class LocalProcessRunner : ILocalProcessRunner
{
    public async Task<ScriptExecutionResult> RunAsync(
        string executable, string arguments, string workDir, CancellationToken ct)
    {
        var logLines = new List<string>();

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

            logLines.Add(e.Data);
            Log.Information("[LocalExec:stdout] {Line}", e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            logLines.Add(e.Data);
            Log.Warning("[LocalExec:stderr] {Line}", e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
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
            ExitCode = exitCode
        };
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill local process");
        }
    }
}
