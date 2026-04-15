using System.Security.Cryptography.X509Certificates;
using Autofac.Extensions.DependencyInjection;
using Squid.Core.Persistence.Db;
using Squid.Core.Settings.Logging;
using Squid.Core.Settings.SelfCert;
using Squid.Core.Settings.System;

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

            new DbUpRunner(new SquidConnectionString(configuration).Value).Run();

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
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
                webBuilder.ConfigureKestrel((ctx, kestrel) =>
                {
                    // Use the same SelfCert as Halibut (port 10943) so clients pinning the Squid
                    // Server thumbprint (returned by MachineScriptService.GetServerThumbprint)
                    // can connect to the HTTP API as well. Without this, Kestrel falls back to
                    // ASP.NET's dev-cert on port 7078 and the thumbprint mismatch breaks TLS pinning.
                    ConfigureSelfCertHttps(kestrel, ctx.Configuration);
                });
            });

    private static void ConfigureSelfCertHttps(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions kestrel, IConfiguration config)
    {
        var selfCert = new SelfCertSetting(config);

        if (string.IsNullOrWhiteSpace(selfCert.Base64)) return;

        try
        {
            var certBytes = Convert.FromBase64String(selfCert.Base64);
            var cert = X509CertificateLoader.LoadPkcs12(certBytes, selfCert.Password);

            kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = cert);

            Log.Information("Kestrel HTTPS configured with SelfCert (thumbprint {Thumbprint})", cert.Thumbprint);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load SelfCert for Kestrel — falling back to ASP.NET default");
        }
    }
}
