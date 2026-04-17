using Serilog;

namespace Squid.Tentacle.ScriptExecution.State;

/// <summary>
/// Process-level exclusive lock on a script workspace.
///
/// Uses <see cref="FileShare.None"/> on a sentinel file inside the workspace —
/// the OS refuses a second open, so two agent instances (or two threads racing
/// the same ticket after redelivery) can't corrupt each other's state.
/// </summary>
public sealed class WorkspaceLock : IDisposable
{
    private const string LockFileName = ".workspace.lock";

    private readonly FileStream _handle;
    private readonly string _lockPath;
    private int _disposed;

    private WorkspaceLock(FileStream handle, string lockPath)
    {
        _handle = handle;
        _lockPath = lockPath;
    }

    public static WorkspaceLock? TryAcquire(string workspace)
    {
        if (!Directory.Exists(workspace))
            Directory.CreateDirectory(workspace);

        var lockPath = Path.Combine(workspace, LockFileName);

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new WorkspaceLock(stream, lockPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static WorkspaceLock Acquire(string workspace, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(25);

        while (true)
        {
            var acquired = TryAcquire(workspace);
            if (acquired != null) return acquired;

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Failed to acquire workspace lock at {workspace} within {timeout.TotalSeconds:F1}s");

            Thread.Sleep(delay);
            if (delay < TimeSpan.FromMilliseconds(200)) delay += delay;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _handle.Dispose(); }
        catch (IOException ex) { Log.Debug(ex, "Failed to release workspace lock at {Path}", _lockPath); }
    }
}
