using Squid.Core.Services.Caching.Redis;
using Squid.Message.Enums.Caching;
using StackExchange.Redis;

namespace Squid.Core.Caching;

public class CachingModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(cfx =>
        {
            var pool = cfx.Resolve<IRedisConnectionPool>();
            return pool.GetConnection();
        }).Keyed<ConnectionMultiplexer>(RedisServer.System).ExternallyOwned();
        
        builder.Register(cfx =>
        {
            var pool = cfx.Resolve<IRedisConnectionPool>();
            return pool.GetConnection(RedisServer.Vector);
        }).Keyed<ConnectionMultiplexer>(RedisServer.Vector).ExternallyOwned();
    }
}