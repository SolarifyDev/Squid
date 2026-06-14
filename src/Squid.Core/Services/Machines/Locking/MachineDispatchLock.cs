using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.Core.Services.Machines.Locking;

/// <summary>
/// Serializes server-side dispatch to a single machine across all pods. A deployment runs its
/// per-machine script under this lock, and a tentacle upgrade already takes the SAME key
/// (<see cref="MachineUpgradeService"/>) — so a deployment and an upgrade targeting the same
/// machine are mutually exclusive: an upgrade restarts the agent, which must never happen while
/// a deployment script is running on it. The lock is a cross-process Redis lock (the only kind
/// that works for a horizontally-scaled multi-pod deployment).
/// </summary>
public interface IMachineDispatchLock : IScopedDependency
{
    /// <summary>
    /// Runs <paramref name="action"/> while holding machine <paramref name="machineId"/>'s
    /// dispatch lock. Throws <see cref="MachineLockUnavailableException"/> if the lock is held
    /// by another active dispatch (contention) or the Redis backend is unreachable — the caller
    /// pauses (resumable) in both cases rather than running unguarded.
    /// </summary>
    Task<T> RunUnderMachineLockAsync<T>(int machineId, Func<Task<T>> action) where T : class;
}

public sealed class MachineDispatchLock : IMachineDispatchLock
{
    private readonly IRedisSafeRunner _redisLock;

    public MachineDispatchLock(IRedisSafeRunner redisLock)
    {
        _redisLock = redisLock;
    }

    // SSOT: the EXACT key the tentacle upgrade dispatch takes
    // (UpgradeDispatchLockReconciler.BuildLockKey), so deploy and upgrade contend on the same
    // lock and cannot run against the same machine at once. Pinned by a unit test.
    internal static string LockKey(int machineId) => UpgradeDispatchLockReconciler.BuildLockKey(machineId);

    public async Task<T> RunUnderMachineLockAsync<T>(int machineId, Func<Task<T>> action) where T : class
    {
        T result;

        try
        {
            // No wait/retry: fail fast on contention so the worker isn't held — the caller pauses
            // resumably instead of blocking for a (potentially minutes-long) upgrade. RedLock
            // auto-extends the lease while held, so a long-running deployment keeps the lock.
            result = await _redisLock.ExecuteWithLockAsync(LockKey(machineId), action).ConfigureAwait(false);
        }
        catch (LockAcquireFailedException ex)
        {
            throw new MachineLockUnavailableException(machineId, isInfrastructureFailure: true,
                "the distributed-lock backend (Redis) is unreachable", ex);
        }

        // ExecuteWithLockAsync returns null ONLY on contention — the action always returns a
        // non-null result — so a null means another active deployment or upgrade holds the lock.
        if (result == null)
            throw new MachineLockUnavailableException(machineId, isInfrastructureFailure: false,
                "another active deployment or upgrade holds the machine's dispatch lock");

        return result;
    }
}
