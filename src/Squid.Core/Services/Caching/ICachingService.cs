namespace Squid.Core.Services.Caching;

public interface ICachingService : IScopedDependency
{
    Task<T> GetAsync<T>(string key, ICachingSetting setting, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync(string key, object data, ICachingSetting setting, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, ICachingSetting setting, CancellationToken cancellationToken = default);
}