using Hangfire;
using Hangfire.Correlate;
using Hangfire.Pro.Redis;
using Newtonsoft.Json;
using Smarties.Api.Extensions.Hangfire;
using Squid.Core.Settings.Caching;

namespace Squid.Api.Extensions.Hangfire;

public class HangfireRegistrarBase : IHangfireRegistrar
{
    public virtual void RegisterHangfire(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHangfire((sp, c) =>
        {
            c.UseThrottling();
            c.UseCorrelate(sp);
            c.UseSimpleAssemblyNameTypeSerializer();
            c.UseMaxArgumentSizeToRender(int.MaxValue);
            c.UseFilter(new AutomaticRetryAttribute { Attempts = 0, LogEvents = false});
            c.UseRedisStorage(new RedisCacheConnectionStringSetting(configuration).Value, 
                new RedisStorageOptions { MaxSucceededListLength = 30000, MaxDeletedListLength = 10000 }).WithJobExpirationTimeout(TimeSpan.FromDays(0.5));
            c.UseSerializerSettings(new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            });
        });
    }

    public virtual void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
    }
}