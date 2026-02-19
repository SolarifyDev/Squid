using Squid.Api.Filters;

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
        services.AddControllers();
        services.AddOptions();
        services.AddCustomSwagger();
        services.AddHttpContextAccessor();
        services.AddLogging();
        services.AddCorsPolicy(Configuration);
        services.AddHangfireInternal(Configuration);
        services.AddMvc(options =>
        {
            options.Filters.Add<GlobalExceptionFilter>();
            options.Filters.Add<GlobalSuccessFilter>();
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "Squid.Api.xml"); });
        }

        app.UseRouting();
        app.UseCors();
        app.UseEndpoints(endpoints => { endpoints.MapControllers(); });
        app.UseHangfireInternal(Configuration);
    }
}
