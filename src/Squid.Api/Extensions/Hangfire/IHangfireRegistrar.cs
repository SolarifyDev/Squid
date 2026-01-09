namespace Smarties.Api.Extensions.Hangfire;

public interface IHangfireRegistrar
{
    void RegisterHangfire(IServiceCollection services, IConfiguration configuration);
    
    void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration);
}