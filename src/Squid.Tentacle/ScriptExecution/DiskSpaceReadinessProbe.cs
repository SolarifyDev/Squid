using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// Readiness wrapper around <see cref="DiskSpaceChecker.HasSufficientSpace"/> that cannot hang the
/// health endpoint. A statfs on a hung hard-mounted NFS volume blocks forever; this runs the check on a
/// pool thread, waits at most <c>timeout</c>, and reports not-ready on timeout. At most ONE check is ever
/// in flight: while a previous one is still stuck, later probes return not-ready immediately instead of
/// stranding another thread each time.
/// </summary>
public sealed class DiskSpaceReadinessProbe
{
    private readonly string _path;
    private readonly TimeSpan _timeout;
    private readonly Func<string, bool> _check;
    private readonly object _gate = new();
    private Task<bool> _inflight;

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
        var task = StartOrNull();

        if (task == null) return false;

        return WaitForResult(task);
    }

    private Task<bool> StartOrNull()
    {
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false })
            {
                Log.Debug("Previous workspace disk check for {Path} is still blocked; reporting not ready", _path);
                return null;
            }

            return _inflight = Task.Run(() => _check(_path));
        }
    }

    private bool WaitForResult(Task<bool> task)
    {
        try
        {
            if (task.Wait(_timeout)) return task.Result;

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
