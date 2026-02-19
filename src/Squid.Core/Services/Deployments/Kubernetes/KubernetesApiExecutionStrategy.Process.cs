using System.Text;

namespace Squid.Core.Services.Deployments.Kubernetes;

public partial class KubernetesApiExecutionStrategy
{
    private static async Task<ScriptExecutionResult> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken ct)
    {
        var logLines = new List<string>();

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            outputBuilder.AppendLine(e.Data);
            logLines.Add(e.Data);
            Log.Information("[LocalExec:stdout] {Line}", e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;

            errorBuilder.AppendLine(e.Data);
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

    private static void TryKillProcess(System.Diagnostics.Process process)
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
