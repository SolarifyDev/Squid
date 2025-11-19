using Squid.Core.Persistence;
using Squid.Core.Settings.SelfCert;

namespace Squid.Api;

public class Startup
{
    private IConfiguration Configuration { get; }

    private IServiceCollection _serviceCollection;

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    // ConfigureServices is where you register dependencies. This gets
    // called by the runtime before the ConfigureContainer method, below.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
        services.AddOptions();
        services.AddCustomSwagger();
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, UserContext>();
        services.AddLogging();
        services.AddHostedService<Squid.Core.Services.Deployments.DeploymentTaskHostedService>();
        services.AddCorsPolicy(Configuration);
        
        _serviceCollection = services;
    }

    // ConfigureContainer is where you can register things directly
    // with Autofac. This runs after ConfigureServices so the things
    // here will override registrations made in ConfigureServices.
    // Don't build the container; that gets done for you by the factory.
    public void ConfigureContainer(ContainerBuilder builder)
    {
        var serviceProvider = _serviceCollection.BuildServiceProvider();

        var userContext = serviceProvider.GetRequiredService<IUserContext>();
        var selfCertSetting = Configuration.GetSection("SelfCert").Get<SelfCertSetting>();

        var storeSetting = Configuration.GetSection("SquidStore").Get<SquidStoreSetting>();

        ApplicationStartup.Initialize(builder, storeSetting, Log.Logger, userContext, Configuration, selfCertSetting);
    }

    // Configure is where you add middleware. This is called after
    // ConfigureContainer. You can use IApplicationBuilder.ApplicationServices
    // here if you need to resolve things from the container.
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
    }
}