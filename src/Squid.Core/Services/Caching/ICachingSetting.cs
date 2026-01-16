using Squid.Message.Enums.Caching;

namespace Squid.Core.Services.Caching;

public interface ICachingSetting
{
    CachingType CachingType { get; }
    
    TimeSpan? Expiry { get; set;  }
}

public class RedisCachingSetting(RedisServer redisServer = RedisServer.System, TimeSpan? expiry = null) : ICachingSetting
{
    public CachingType CachingType => CachingType.RedisCache;

    public RedisServer RedisServer => redisServer;

    public TimeSpan? Expiry { get => expiry; set => expiry = value; }
}

public class MemoryCachingSetting(TimeSpan? expiry = null) : ICachingSetting
{
    public CachingType CachingType => CachingType.MemoryCache;
    
    public TimeSpan? Expiry { get => expiry; set => expiry = value; }
}