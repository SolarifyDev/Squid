using Squid.Core.Services.Caching.Redis;
using Squid.Message.Enums.Caching;

namespace Squid.Core.Services.Caching;

public interface ICacheManager : IScopedDependency
{
    Task<T> GetAsync<T>(string key, ICachingSetting setting, CancellationToken cancellationToken) where T : class;
    
    Task<T> UsingCacheAsync<T>(string key, ICachingSetting setting, Func<string, Task<T>> whenNotFound, CancellationToken cancellationToken = default) where T : class;

    Task<T> GetOrAddAsync<T>(string key, Func<string, Task<T>> whenNotFound, ICachingSetting setting, CancellationToken cancellationToken = default) where T : class;
    
    Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> whenNotFound, ICachingSetting setting, CancellationToken cancellationToken = default) where T : class;
    
    Task SetAsync(string key, object data, ICachingSetting setting, CancellationToken cancellationToken = default);
    
    Task RemoveAsync(string key, ICachingSetting setting, CancellationToken cancellationToken = default);
}

public class CacheManager : ICacheManager
{
    private readonly MemoryCacheService _memoryCacheService;
    private readonly RedisCacheService _redisCacheService;

    public CacheManager(MemoryCacheService memoryCacheService, RedisCacheService redisCacheService)
    {
        _memoryCacheService = memoryCacheService;
        _redisCacheService = redisCacheService;
    }

    public async Task<T> GetAsync<T>(string key, ICachingSetting setting, CancellationToken cancellationToken) where T : class
    {
        var cachingService = GetCachingService(setting);

        return await cachingService.GetAsync<T>(key, setting, cancellationToken).ConfigureAwait(false);
    }
    
    public async Task<T> UsingCacheAsync<T>(string key, ICachingSetting setting, Func<string, Task<T>> whenNotFound, CancellationToken cancellationToken = default) where T : class
    {
        var result = await _memoryCacheService.GetAsync<T>(key, setting, cancellationToken).ConfigureAwait(false);
        
        if (result != null)
            return result;

        setting.Expiry ??= TimeSpan.FromMinutes(5);
        
        result = await GetOrAddAsync(key, whenNotFound, setting, cancellationToken).ConfigureAwait(false);
        
        await _memoryCacheService.SetAsync(key, result, setting, cancellationToken).ConfigureAwait(false);
        
        return result;
    }
    
    public async Task<T> GetOrAddAsync<T>(string key, Func<string, Task<T>> whenNotFound, 
        ICachingSetting setting, CancellationToken cancellationToken = default) where T: class
    {
        var cachedResult = await GetAsync<T>(key, setting, cancellationToken).ConfigureAwait(false);

        if (cachedResult != null) return cachedResult;

        var result = await whenNotFound(key);

        await SetAsync(key, result, setting, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> whenNotFound, 
        ICachingSetting setting, CancellationToken cancellationToken = default) where T : class
    {
        var cachedResult = await GetAsync<T>(key, setting, cancellationToken).ConfigureAwait(false);

        if (cachedResult != null) return cachedResult;

        var result = await whenNotFound();

        await SetAsync(key, result, setting, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task SetAsync(string key, object data, 
        ICachingSetting setting, CancellationToken cancellationToken = default)
    {
        var cachingService = GetCachingService(setting);
        
        await cachingService.SetAsync(key, data, setting, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, ICachingSetting setting, CancellationToken cancellationToken = default)
    {
        var cachingService = GetCachingService(setting);
        
        await cachingService.RemoveAsync(key, setting, cancellationToken).ConfigureAwait(false);
    }

    private ICachingService GetCachingService(ICachingSetting settings)
    {
        return settings.CachingType switch
        {
            CachingType.RedisCache => _redisCacheService,
            CachingType.MemoryCache => _memoryCacheService
        };
    }
}