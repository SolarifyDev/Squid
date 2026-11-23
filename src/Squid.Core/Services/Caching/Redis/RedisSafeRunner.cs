using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;
using RedLockNet.SERedis.Configuration;
using Squid.Message.Enums.Caching;

namespace Squid.Core.Services.Caching.Redis;

public interface IRedisSafeRunner : IScopedDependency
{
    Task ExecuteAsync(Func<ConnectionMultiplexer, Task> func, RedisServer server = RedisServer.System);

    Task<T> ExecuteAsync<T>(Func<ConnectionMultiplexer, Task<T>> func, RedisServer server = RedisServer.System) where T : class;

    Task<List<T>> ExecuteAsync<T>(Func<ConnectionMultiplexer, Task<List<T>>> func, RedisServer server = RedisServer.System) where T : class;

    Task ExecuteWithLockAsync(string lockKey, Func<Task> logic,
        TimeSpan? expiry = null, TimeSpan? wait = null, TimeSpan? retry = null, RedisServer server = RedisServer.System);
    
    Task<T> ExecuteWithLockAsync<T>(string lockKey, Func<Task<T>> logic,
        TimeSpan? expiry = null, TimeSpan? wait = null, TimeSpan? retry = null, RedisServer server = RedisServer.System) where T : class;
}
    
public class RedisSafeRunner : IRedisSafeRunner
{
    private readonly Func<RedisServer, ConnectionMultiplexer> _connectionResolver;

    public RedisSafeRunner(IComponentContext context)
    {
        var vectorRedis = context.ResolveKeyed<ConnectionMultiplexer>(RedisServer.Vector);
        var systemRedis = context.ResolveKeyed<ConnectionMultiplexer>(RedisServer.System);

        _connectionResolver = server => server switch
        {
            RedisServer.Vector => vectorRedis,
            RedisServer.System or _ => systemRedis
        };
    }

    public async Task ExecuteAsync(Func<ConnectionMultiplexer, Task> func, RedisServer server = RedisServer.System)
    {
        try
        {
            await func(_connectionResolver(server)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisException(ex);
        }
    }
    
    public async Task<T> ExecuteAsync<T>(Func<ConnectionMultiplexer, Task<T>> func, RedisServer server = RedisServer.System) where T : class
    {
        try
        {
            return await func(_connectionResolver(server)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisException(ex);
            return default;
        }
    }
    
    public async Task<List<T>> ExecuteAsync<T>(Func<ConnectionMultiplexer, Task<List<T>>> func, RedisServer server = RedisServer.System) where T : class
    {
        try
        {
            return await func(_connectionResolver(server)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisException(ex);
            return new List<T>();
        }
    }

    public async Task ExecuteWithLockAsync(string lockKey, Func<Task> logic, TimeSpan? expiry = null, TimeSpan? wait = null,
        TimeSpan? retry = null, RedisServer server = RedisServer.System)
    {
        try
        {
            var redLock = await CreateLockAsync(lockKey, expiry, wait, retry, server).ConfigureAwait(false);
        
            await using (redLock)
            {
                // make sure we got the lock
                if (redLock.IsAcquired)
                    await logic();
            }
        }
        catch (Exception ex)
        {
            LogRedisException(ex);
        }
    }

    public async Task<T> ExecuteWithLockAsync<T>(string lockKey, Func<Task<T>> logic,
        TimeSpan? expiry = null, TimeSpan? wait = null, TimeSpan? retry = null, RedisServer server = RedisServer.System) where T : class
    {
        // Three outcomes, three distinct signals (1.6.x audit P0-F3):
        //   1. Infra failure (Redis down, TLS refused, multiplexer disposed):
        //      throw LockAcquireFailedException so the caller can surface
        //      an infrastructure-problem message instead of masking it as
        //      contention.
        //   2. Contention (lock held by another caller): return default(T).
        //      Unchanged contract — existing callers already interpret null
        //      as "another operator got there first".
        //   3. Success: return the logic's result. If the logic itself
        //      throws, the exception PROPAGATES (pre-fix code swallowed
        //      it, masking upgrade-strategy failures as contention).
        IRedLock redLock;
        try
        {
            EnsureConnected(server);

            redLock = await CreateLockAsync(lockKey, expiry, wait, retry, server).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisException(ex);
            throw new LockAcquireFailedException(lockKey, ex);
        }

        await using (redLock)
        {
            if (!redLock.IsAcquired)
                return ResolveUnacquired<T>(lockKey, redLock);

            return await logic().ConfigureAwait(false);
        }
    }

    // RedLock.net never throws when Redis is unreachable (abortConnect=False): it returns IsAcquired=false with Status NoQuorum
    // (or Unlocked). Only Conflicted means another holder owns the lock; every other status is an infrastructure failure.
    internal static T ResolveUnacquired<T>(string lockKey, IRedLock redLock) where T : class
    {
        if (redLock.Status == RedLockStatus.Conflicted) return default;

        var inner = new InvalidOperationException($"Redis lock was not acquired and no holder was found (status {redLock.Status}); Redis is unreachable or has no quorum");

        LogRedisException(inner);

        throw new LockAcquireFailedException(lockKey, inner);
    }

    // A multiplexer that is known to be disconnected would otherwise make RedLock retry NoQuorum for the caller's whole wait.
    private void EnsureConnected(RedisServer server)
    {
        if (!_connectionResolver(server).IsConnected)
            throw new InvalidOperationException("Redis connection is down");
    }

    private async Task<IRedLock> CreateLockAsync(string lockKey, TimeSpan? expiry = null, TimeSpan? wait = null, TimeSpan? retry = null, RedisServer server = RedisServer.System)
    {
        var multiplexers = new List<RedLockMultiplexer> { _connectionResolver(server) };
        // The factory subscribes to the shared multiplexer's ConfigurationChanged and only unsubscribes in Dispose, so one that is never
        // disposed leaks a handler (and itself) per call. Disposing it does not touch the multiplexer or any lock it created: a lock holds
        // its own connections and keepalive timer, and the existing-multiplexer provider's DisposeConnections is a no-op.
        using var redLockFactory = RedLockFactory.Create(multiplexers);

        var expiryTime = expiry ?? TimeSpan.FromSeconds(30);
        var waitTime = wait ?? TimeSpan.FromSeconds(10);
        var retryTime = retry ?? TimeSpan.FromSeconds(1);

        IRedLock redLock;

        if (wait.HasValue && retry.HasValue)
            redLock = await redLockFactory.CreateLockAsync(lockKey, expiryTime, waitTime, retryTime).ConfigureAwait(false);
        else
            redLock = await redLockFactory.CreateLockAsync(lockKey, expiryTime).ConfigureAwait(false);

        return redLock;
    }

    private static void LogRedisException(Exception ex)
    {
        Log.Error(ex, "Redis occur error: {ErrorMessage}", ex.Message);
    }
}