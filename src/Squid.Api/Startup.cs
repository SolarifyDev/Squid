using Correlate.AspNetCore;
using Correlate.DependencyInjection;
using Squid.Api.Filters;
using Squid.Core.Constants;

namespace Squid.Api;

public class Startup
{
    private IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddCorrelate(options => options.RequestHeaders = SquidApiConstants.CorrelationIdHeaders);
        services.AddControllers();
        services.AddOptions();
        services.AddCustomSwagger();
        services.AddHttpContextAccessor();
        services.AddHttpClientInternal();
        services.AddLogging();
        services.AddCorsPolicy(Configuration);
        services.AddCustomAuthentication(Configuration);
        services.AddSquidHangfire(Configuration);
        services.AddMvc(options =>
        {
            options.Filters.Add<GlobalExceptionFilter>();
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "Squid.Api.xml"); });
        }

        app.UseSerilogRequestLogging();
        app.UseCorrelate();
        app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => { endpoints.MapControllers(); });
        app.UseSquidHangfire(Configuration);
    }
}
