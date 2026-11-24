using System.Diagnostics;
using System.Text;

namespace Squid.Core.Setup.Embedded;

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct);
}

public sealed record ProcessRunRequest(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);

public sealed record ProcessRunResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a short-lived helper (initdb, pg_ctl, chown, redis-server --daemonize) to completion and
/// returns its combined output. Long-running servers daemonise themselves, so nothing started here
/// outlives this call as a child we must track.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct)
    {
        using var process = new Process { StartInfo = BuildStartInfo(request) };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitWithTimeoutAsync(process, request, ct).ConfigureAwait(false);

        return new ProcessRunResult(process.ExitCode, output.ToString().Trim());
    }

    private static ProcessStartInfo BuildStartInfo(ProcessRunRequest request)
    {
        var info = new ProcessStartInfo(request.FileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);

        return info;
    }

    private static async Task WaitWithTimeoutAsync(Process process, ProcessRunRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{request.FileName}' did not finish within {request.Timeout.TotalSeconds:0}s");
        }
    }

    private static void Append(StringBuilder output, string line)
    {
        if (line == null) return;

        lock (output) output.AppendLine(line);
    }
}
