using Autofac.Extensions.DependencyInjection;
using Squid.Core.DbUpFiles;
using Squid.Core.Settings;
using Squid.Core.Settings.Logging;

namespace Squid.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .Build();

        var apiKey = new SerilogApiKeySetting(configuration).Value;
        var serverUrl = new SerilogServerUrlSetting(configuration).Value;
        var application = new SerilogApplicationSetting(configuration).Value;

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .WriteTo.Console()
            .WriteTo.Seq(serverUrl, apiKey: apiKey)
            .CreateLogger();

        try
        {
            Log.Information("Configuring api host ({ApplicationContext})...", application);

            new DbUpRunner(new SquidConnectionString(configuration).Value).Run(nameof(Core.DbUpFiles), typeof(DbUpRunner).Assembly);

            var webHost = CreateHostBuilder(args, configuration).Build();

            Log.Information("Starting api host ({ApplicationContext})...", application);

            webHost.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Program terminated unexpectedly ({ApplicationContext})!", application);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args, IConfiguration configuration) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureLogging(l => l.AddSerilog(Log.Logger))
            .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureContainer<ContainerBuilder>(builder =>
            {
                builder.RegisterModule(new SquidModule(Log.Logger, configuration));
            })
            .ConfigureWebHostDefaults(webBuilder => { webBuilder.UseStartup<Startup>(); });
}
