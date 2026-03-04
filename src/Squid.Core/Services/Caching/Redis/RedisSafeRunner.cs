using Serilog;
using Autofac;
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
        try
        {
            var redLock = await CreateLockAsync(lockKey, expiry, wait, retry, server).ConfigureAwait(false);

            await using (redLock)
            {
                // make sure we got the lock
                if (redLock.IsAcquired)
                    return await logic();
            }
        
            return default;
        }
        catch (Exception ex)
        {
            LogRedisException(ex);
            return default;
        }
    }

    private async Task<IRedLock> CreateLockAsync(string lockKey, TimeSpan? expiry = null, TimeSpan? wait = null, TimeSpan? retry = null, RedisServer server = RedisServer.System)
    {
        var multiplexers = new List<RedLockMultiplexer> { _connectionResolver(server) };
        var redLockFactory = RedLockFactory.Create(multiplexers);

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