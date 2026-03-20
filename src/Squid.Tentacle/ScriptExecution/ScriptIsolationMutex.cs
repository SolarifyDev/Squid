using System.Collections.Concurrent;
using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public class ScriptIsolationMutex
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutexes = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(StartScriptCommand command, out IDisposable? handle)
    {
        if (command.Isolation != ScriptIsolationLevel.FullIsolation)
        {
            handle = NoOpDisposable.Instance;
            return true;
        }

        var mutexName = command.IsolationMutexName ?? "default";
        var semaphore = _mutexes.GetOrAdd(mutexName, _ => new SemaphoreSlim(1, 1));

        if (!semaphore.Wait(TimeSpan.Zero))
        {
            Log.Information("Isolation mutex {MutexName} is held, deferring script", mutexName);
            handle = null;
            return false;
        }

        Log.Information("Acquired isolation mutex {MutexName}", mutexName);

        handle = new MutexRelease(semaphore, mutexName);
        return true;
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
