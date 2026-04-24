using Newtonsoft.Json;

namespace Squid.Core.Services.Caching.Redis;

public class RedisCacheService : ICachingService
{
    /// <summary>
    /// JSON serializer settings used for every Redis round-trip.
    /// <para>Pinned to <see cref="TypeNameHandling.None"/> — the default.
    /// Pre-1.6.x code used <c>TypeNameHandling.Auto</c>, which instructs
    /// Newtonsoft to honour a <c>$type</c> field in the input JSON and
    /// instantiate whatever runtime type that string names. If a Redis
    /// instance hosting Squid's cache is ever compromised (shared
    /// tenancy, misconfigured auth, network exposure, supply-chain on
    /// the sidecar), an attacker who can write a single key turns every
    /// authenticated API-key cache lookup into remote code execution in
    /// the Squid server process via gadget chains like
    /// <c>System.Windows.Data.ObjectDataProvider</c>.</para>
    ///
    /// <para>Safety of the switch: grep of production callers
    /// (<c>RequestCachingSpecification</c>, <c>ApiKeyAuthenticationHandler</c>)
    /// shows nobody depends on polymorphic nested-field round-trips.
    /// Old cache entries containing <c>$type</c> fields still deserialise
    /// correctly — <c>None</c> just ignores the extra field. No migration
    /// needed.</para>
    ///
    /// <para>Pinned by <c>RedisCacheServiceTypeHandlingTests</c> — any
    /// refactor that flips this back to a permissive value fails in CI.</para>
    /// </summary>
    internal static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None
    };

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
                ? typeof(T) == typeof(string) ? cachedResult.ToString() as T : JsonConvert.DeserializeObject<T>(cachedResult, JsonSettings)
                : null;
        }, ((RedisCachingSetting)setting).RedisServer).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, object data, ICachingSetting setting, CancellationToken cancellationToken = default)
    {
        await _redisSafeRunner.ExecuteAsync(async redisConnection =>
        {
            if (data != null)
            {
                var stringValue = data as string ?? JsonConvert.SerializeObject(data, JsonSettings);
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