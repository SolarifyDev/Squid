using System.Collections.Concurrent;
using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public class ScriptIsolationMutex
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutexes = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Acquire(StartScriptCommand command)
    {
        if (command.Isolation != ScriptIsolationLevel.FullIsolation)
            return NoOpDisposable.Instance;

        var mutexName = command.IsolationMutexName ?? "default";
        var timeout = command.ScriptIsolationMutexTimeout;

        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromMinutes(5);

        var semaphore = _mutexes.GetOrAdd(mutexName, _ => new SemaphoreSlim(1, 1));

        Log.Information("Acquiring isolation mutex {MutexName} (timeout {TimeoutSeconds}s)", mutexName, timeout.TotalSeconds);

        if (!semaphore.Wait(timeout))
        {
            throw new TimeoutException(
                $"Timed out waiting {timeout.TotalSeconds}s for script isolation mutex '{mutexName}'. " +
                "Another script with FullIsolation is still running.");
        }

        Log.Information("Acquired isolation mutex {MutexName}", mutexName);

        return new MutexRelease(semaphore, mutexName);
    }

    private sealed class MutexRelease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly string _mutexName;
        private int _disposed;

        public MutexRelease(SemaphoreSlim semaphore, string mutexName)
        {
            _semaphore = semaphore;
            _mutexName = mutexName;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();

                Log.Information("Released isolation mutex {MutexName}", _mutexName);
            }
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
