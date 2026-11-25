using System.Diagnostics;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// Readiness wrapper around <see cref="DiskSpaceChecker.HasSufficientSpace"/> that cannot hang the
/// health endpoint. A statfs on a hung hard-mounted NFS volume blocks forever; this runs the check on a
/// pool thread, waits at most <c>timeout</c>, and reports not-ready on timeout. At most ONE check is ever
/// in flight, and overlapping probes share it: a later probe waits for the in-flight check only for what is
/// left of ITS timeout budget (measured from when that check started), so a healthy check that is merely
/// still running does not flap readiness, while a check stuck past its budget returns not-ready immediately
/// instead of stranding another thread (or another full timeout) per probe.
/// </summary>
public sealed class DiskSpaceReadinessProbe
{
    private readonly string _path;
    private readonly TimeSpan _timeout;
    private readonly Func<string, bool> _check;
    private readonly object _gate = new();
    private Task<bool> _inflight;
    private long _inflightStartedAt;

    public DiskSpaceReadinessProbe(string path, int timeoutSeconds, Func<string, bool> check = null)
    {
        _path = path;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : TentacleSettings.DefaultDiskSpaceReadinessTimeoutSeconds);
        _check = check ?? DiskSpaceChecker.HasSufficientSpace;
    }

    internal DiskSpaceReadinessProbe(string path, TimeSpan timeout, Func<string, bool> check)
    {
        _path = path;
        _timeout = timeout;
        _check = check;
    }

    public bool IsReady()
    {
        var (task, budget) = StartOrJoin();

        if (budget <= TimeSpan.Zero && !task.IsCompleted)
        {
            Log.Debug("Previous workspace disk check for {Path} has used its whole budget; reporting not ready", _path);
            return false;
        }

        return WaitForResult(task, budget);
    }

    private (Task<bool> Task, TimeSpan Budget) StartOrJoin()
    {
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false })
                return (_inflight, _timeout - Stopwatch.GetElapsedTime(_inflightStartedAt));

            _inflightStartedAt = Stopwatch.GetTimestamp();

            return (_inflight = Task.Run(() => _check(_path)), _timeout);
        }
    }

    private bool WaitForResult(Task<bool> task, TimeSpan budget)
    {
        try
        {
            if (task.Wait(budget > TimeSpan.Zero ? budget : TimeSpan.Zero)) return task.Result;

            Log.Warning("Workspace disk check for {Path} did not return within {Timeout}; reporting not ready (is the volume hung?)", _path, _timeout);
            return false;
        }
        catch (AggregateException ex)
        {
            Log.Warning(ex.InnerException ?? ex, "Workspace disk check for {Path} failed; reporting not ready", _path);
            return false;
        }
    }
}
