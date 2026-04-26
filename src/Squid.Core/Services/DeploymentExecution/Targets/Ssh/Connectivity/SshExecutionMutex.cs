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

    private readonly ConcurrentDictionary<string, CountedSemaphore> _locks = new();

    /// <summary>
    /// Number of live lock entries in the internal dictionary. Exposed for
    /// tests to assert the eviction contract — production code should not
    /// rely on this.
    /// </summary>
    internal int LockCount => _locks.Count;

    public async Task<IDisposable> AcquireAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var key = $"{host}:{port}".ToLowerInvariant();
        var counted = AcquireRef(key);

        Log.Debug("[SSH] Acquiring execution lock for {Endpoint}", key);

        try
        {
            var acquired = await counted.Semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);

            if (!acquired)
                throw new TimeoutException($"Timed out waiting for SSH execution lock on {key} after {timeout.TotalSeconds}s");
        }
        catch
        {
            // Timeout or cancellation: never got the semaphore, so we don't
            // need to release it. We DO need to drop the ref so the entry
            // can be evicted if we were the last waiter.
            ReleaseRef(key, counted, semaphoreWasAcquired: false);
            throw;
        }

        Log.Debug("[SSH] Acquired execution lock for {Endpoint}", key);

        return new LockRelease(this, key, counted);
    }

    /// <summary>
    /// P1-N3 (Phase-7): atomically gets-or-adds the per-endpoint slot and
    /// bumps its refcount. The refcount tracks every concurrent caller of
    /// <see cref="AcquireAsync"/> for this endpoint — both the holder and
    /// any waiters. When the last caller releases (refcount drops to 0),
    /// the entry is removed from the dict and its <see cref="SemaphoreSlim"/>
    /// is disposed. This closes the unbounded growth that pre-fix leaked
    /// one <see cref="SemaphoreSlim"/> per ever-seen <c>host:port</c> for
    /// the lifetime of the singleton service.
    /// </summary>
    private CountedSemaphore AcquireRef(string key)
    {
        while (true)
        {
            var counted = _locks.GetOrAdd(key, _ => new CountedSemaphore());
            Interlocked.Increment(ref counted.RefCount);

            // Verify our handle is still the live entry — another thread
            // might have evicted it between GetOrAdd and the increment.
            if (_locks.TryGetValue(key, out var current) && ReferenceEquals(current, counted))
                return counted;

            // Race-lost: drop the ref and retry. Don't dispose — whoever
            // still holds a reference will dispose when they finish.
            Interlocked.Decrement(ref counted.RefCount);
        }
    }

    private void ReleaseRef(string key, CountedSemaphore counted, bool semaphoreWasAcquired)
    {
        if (semaphoreWasAcquired)
            counted.Semaphore.Release();

        if (Interlocked.Decrement(ref counted.RefCount) > 0) return;

        // Last ref — try to evict. The KeyValuePair overload of TryRemove
        // checks reference equality on the value, so we won't accidentally
        // remove a re-added entry that happens to share the same key.
        if (_locks.TryRemove(new KeyValuePair<string, CountedSemaphore>(key, counted)))
        {
            counted.Semaphore.Dispose();
            Log.Debug("[SSH] Evicted idle execution lock for {Endpoint}", key);
        }
    }

    private sealed class CountedSemaphore
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }

    private sealed class LockRelease : IDisposable
    {
        private readonly SshExecutionMutex _owner;
        private readonly string _key;
        private readonly CountedSemaphore _counted;
        private int _disposed;

        public LockRelease(SshExecutionMutex owner, string key, CountedSemaphore counted)
        {
            _owner = owner;
            _key = key;
            _counted = counted;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            _owner.ReleaseRef(_key, _counted, semaphoreWasAcquired: true);
            Log.Debug("[SSH] Released execution lock for {Endpoint}", _key);
        }
    }
}
