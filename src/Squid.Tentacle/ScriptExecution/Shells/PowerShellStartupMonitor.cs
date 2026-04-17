using System.Diagnostics;
using Serilog;

namespace Squid.Tentacle.ScriptExecution.Shells;

/// <summary>
/// Detects the classic Windows PowerShell "cold-start hang" failure mode:
/// on a fresh VM, PowerShell 5.1 and occasionally pwsh 7 can block for minutes
/// (or indefinitely) before executing the first line of a script due to module
/// discovery, execution-policy checks, or PSReadLine import stalls.
///
/// The mitigation, borrowed from Octopus Tentacle, is a sentinel handshake:
///   - Before the real script runs, the bootstrap touches a sentinel file.
///   - This monitor watches for that file; if it does not appear within
///     <paramref name="timeout"/>, the process tree is killed and the caller
///     receives a specific exit code so the failure is diagnosable rather than
///     appearing as an indistinguishable hang.
/// </summary>
public sealed class PowerShellStartupMonitor : IDisposable
{
    public const string SentinelFileName = "shouldrun.txt";

    private readonly Process _process;
    private readonly string _sentinelPath;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<StartupOutcome> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _watchTask = Task.CompletedTask;
    private int _disposed;

    public PowerShellStartupMonitor(Process process, string workspace, TimeSpan timeout)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _sentinelPath = Path.Combine(workspace, SentinelFileName);
        _timeout = timeout;
    }

    /// <summary>
    /// Path the bootstrap script should create as soon as PowerShell reaches user code.
    /// </summary>
    public string SentinelPath => _sentinelPath;

    /// <summary>
    /// Starts the background watcher. Completes when either the sentinel appears,
    /// the process exits, or the timeout fires.
    /// </summary>
    public Task<StartupOutcome> StartAsync()
    {
        _watchTask = Task.Run(WatchLoop);
        return _tcs.Task;
    }

    /// <summary>
    /// Produces the wrapper that prepends the sentinel touch to a user PowerShell script.
    /// </summary>
    public static string WrapScript(string userScript, string sentinelPath)
    {
        var escapedPath = sentinelPath.Replace("'", "''");
        return $"$null = New-Item -Force -Path '{escapedPath}'\n{userScript}";
    }

    private async Task WatchLoop()
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        var pollInterval = TimeSpan.FromMilliseconds(100);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (File.Exists(_sentinelPath))
                {
                    _tcs.TrySetResult(StartupOutcome.SentinelAppeared);
                    return;
                }

                if (_process.HasExited)
                {
                    _tcs.TrySetResult(StartupOutcome.ProcessExitedBeforeSentinel);
                    return;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Log.Warning("PowerShell startup hang detected — sentinel {SentinelPath} did not appear within {TimeoutSeconds:F0}s. Killing process tree.",
                        _sentinelPath, _timeout.TotalSeconds);
                    KillProcessTree();
                    _tcs.TrySetResult(StartupOutcome.TimedOut);
                    return;
                }

                await Task.Delay(pollInterval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _tcs.TrySetResult(StartupOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PowerShell startup monitor crashed — assuming startup completed");
            _tcs.TrySetResult(StartupOutcome.MonitorError);
        }
    }

    private void KillProcessTree()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to kill PowerShell process {Pid} during startup-hang handling", _process.Id);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); _cts.Dispose(); }
        catch { /* ignore */ }

        try { _watchTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* ignore */ }
    }
}

public enum StartupOutcome
{
    SentinelAppeared,
    TimedOut,
    ProcessExitedBeforeSentinel,
    Cancelled,
    MonitorError
}
