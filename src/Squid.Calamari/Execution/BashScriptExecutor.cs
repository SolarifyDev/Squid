using System.Diagnostics;

namespace Squid.Calamari.Execution;

/// <summary>
/// Executes a bash script file, streaming stdout/stderr through ScriptOutputProcessor.
/// </summary>
public class BashScriptExecutor
{
    public async Task<int> ExecuteAsync(
        string scriptPath,
        string workDir,
        ScriptOutputProcessor outputProcessor,
        CancellationToken ct)
    {
        var process = CreateProcess(scriptPath, workDir);

        process.OutputDataReceived += (_, e) => outputProcessor.ProcessLine(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => outputProcessor.ProcessLine(e.Data, isError: true);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return process.ExitCode;
    }

    private static Process CreateProcess(string scriptPath, string workDir)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"\"{scriptPath}\"",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
    }
}
