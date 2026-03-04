using Squid.Api.Extensions.Hangfire;

namespace Squid.Api.Extensions;

public static class HangfireExtension
{
    public static void AddSquidHangfire(this IServiceCollection services, IConfiguration configuration)
    {
        new SquidHangfireRegistrar().RegisterHangfire(services, configuration);
    }

    public static void UseSquidHangfire(this IApplicationBuilder app, IConfiguration configuration)
    {
        new SquidHangfireRegistrar().ApplyHangfire(app, configuration);
    }
}
