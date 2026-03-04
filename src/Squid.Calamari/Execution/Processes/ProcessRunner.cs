using System.Diagnostics;
using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Execution.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> ExecuteAsync(
        ProcessInvocation invocation,
        IProcessOutputSink outputSink,
        CancellationToken ct)
    {
        using var process = CreateProcess(invocation);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                outputSink.WriteStdout(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                outputSink.WriteStderr(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode);
    }

    private static Process CreateProcess(ProcessInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in invocation.Arguments)
            startInfo.ArgumentList.Add(arg);

        if (invocation.EnvironmentVariables != null)
        {
            foreach (var (key, value) in invocation.EnvironmentVariables)
                startInfo.Environment[key] = value;
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }
}
