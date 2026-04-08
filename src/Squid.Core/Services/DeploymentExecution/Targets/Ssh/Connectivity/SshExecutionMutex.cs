using System.Collections.Concurrent;
using Serilog;
using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public interface ISshExecutionMutex : ISingletonDependency
{
    Task<IDisposable> AcquireAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
}

public class SshExecutionMutex : ISshExecutionMutex
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var key = $"{host}:{port}".ToLowerInvariant();
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        Log.Debug("[SSH] Acquiring execution lock for {Endpoint}", key);

        var acquired = await semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);

        if (!acquired)
            throw new TimeoutException($"Timed out waiting for SSH execution lock on {key} after {timeout.TotalSeconds}s");

        Log.Debug("[SSH] Acquired execution lock for {Endpoint}", key);

        return new LockRelease(semaphore, key);
    }

    private sealed class LockRelease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly string _key;
        private int _disposed;

        public LockRelease(SemaphoreSlim semaphore, string key)
        {
            _semaphore = semaphore;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            _semaphore.Release();
            Log.Debug("[SSH] Released execution lock for {Endpoint}", _key);
        }
    }
}
