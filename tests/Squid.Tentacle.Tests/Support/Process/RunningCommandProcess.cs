using System.Diagnostics;
using System.Text;

namespace Squid.Tentacle.Tests.Support.Process;

public sealed class RunningCommandProcess : IAsyncDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _sync = new();

    private RunningCommandProcess(System.Diagnostics.Process process)
    {
        _process = process;
    }

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public string StdOut
    {
        get
        {
            lock (_sync)
                return _stdout.ToString();
        }
    }

    public string StdErr
    {
        get
        {
            lock (_sync)
                return _stderr.ToString();
        }
    }

    public string CombinedOutput => $"{StdOut}{System.Environment.NewLine}{StdErr}";

    public static RunningCommandProcess Start(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment = null)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (environment != null)
        {
            foreach (var (key, value) in environment)
                process.StartInfo.Environment[key] = value;
        }

        var running = new RunningCommandProcess(process);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            lock (running._sync)
                running._stdout.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            lock (running._sync)
                running._stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName} {arguments}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return running;
    }

    public async Task<bool> WaitForOutputContainsAsync(
        string text,
        TimeSpan timeout,
        CancellationToken ct,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (CombinedOutput.Contains(text, comparison))
                return true;

            if (_process.HasExited)
                return CombinedOutput.Contains(text, comparison);

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return CombinedOutput.Contains(text, comparison);
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        await _process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void Kill()
    {
        if (_process.HasExited)
            return;

        _process.Kill(entireProcessTree: true);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
