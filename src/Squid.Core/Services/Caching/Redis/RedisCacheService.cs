using Newtonsoft.Json;
using StackExchange.Redis;

namespace Squid.Core.Services.Caching.Redis;

public class RedisCacheService : ICachingService
{
    private readonly IRedisSafeRunner _redisSafeRunner;

    public RedisCacheService(IRedisSafeRunner redisSafeRunner)
    {
        _redisSafeRunner = redisSafeRunner;
    }

    public async Task<T> GetAsync<T>(string key, ICachingSetting setting, CancellationToken cancellationToken = default) where T : class
    {
        return await _redisSafeRunner.ExecuteAsync(async redisConnection =>
        {
            var cachedResult = await redisConnection.GetDatabase().StringGetAsync(key).ConfigureAwait(false);
            return !cachedResult.IsNullOrEmpty
                ? typeof(T) == typeof(string) ? cachedResult.ToString() as T : JsonConvert.DeserializeObject<T>(cachedResult, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto })
                : null;
        }, ((RedisCachingSetting)setting).RedisServer).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, object data, ICachingSetting setting, CancellationToken cancellationToken = default)
    {
        await _redisSafeRunner.ExecuteAsync(async redisConnection =>
        {
            if (data != null)
            {
                var stringValue = data as string ?? JsonConvert.SerializeObject(data, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                await redisConnection.GetDatabase().StringSetAsync(key, stringValue, setting.Expiry).ConfigureAwait(false);
            }
        }, ((RedisCachingSetting)setting).RedisServer).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, ICachingSetting setting, CancellationToken cancellationToken = default)
    {
        await _redisSafeRunner.ExecuteAsync(async redisConnection =>
        {
            var db = redisConnection.GetDatabase();
            await db.KeyDeleteAsync(key);
        }, ((RedisCachingSetting)setting).RedisServer).ConfigureAwait(false);
    }
}