using Smarties.Api.Extensions.Hangfire;
using Squid.Api.Extensions.Hangfire;
using Squid.Core.Settings.System;
using Squid.Message.Enums.System;

namespace Squid.Api.Extensions;

public static class HangfireExtension
{
    public static void AddHangfireInternal(this IServiceCollection services, IConfiguration configuration)
    {
        var hangfireRegistrar = FindRegistrar(configuration);

        hangfireRegistrar.RegisterHangfire(services, configuration);
    }

    public static void UseHangfireInternal(this IApplicationBuilder app, IConfiguration configuration)
    {
        var hangfireRegistrar = FindRegistrar(configuration);

        hangfireRegistrar.ApplyHangfire(app, configuration);
    }

    private static IHangfireRegistrar FindRegistrar(IConfiguration configuration)
    {
        var hangfireHosting = new HangfireHostingSetting(configuration).Value;
        
        return hangfireHosting switch
        {
            HangfireHosting.Api => new ApiHangfireRegistrar(),
            HangfireHosting.Internal => new InternalHangfireRegistrar(),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}