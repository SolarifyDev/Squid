using Microsoft.Extensions.Caching.Memory;

namespace Squid.Core.Services.Caching;

public class MemoryCacheService : ICachingService
{
    private readonly IMemoryCache _memoryCache;

    public MemoryCacheService(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }

    public Task<T> GetAsync<T>(string key, ICachingSetting setting, CancellationToken cancellationToken = default) where T : class
    {
        _memoryCache.TryGetValue<T>(key, out var result);

        return Task.FromResult(result);
    }

    public Task SetAsync(string key, object data, ICachingSetting setting, CancellationToken cancellationToken = default)
    {
        if (!setting.Expiry.HasValue)
            _memoryCache.Set(key, data);
        else
            _memoryCache.Set(key, data, setting.Expiry.Value);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, ICachingSetting setting, CancellationToken cancellationToken = default)
    {
        _memoryCache.Remove(key);

        return Task.CompletedTask;
    }
}