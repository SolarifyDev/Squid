using System.Security.Cryptography.X509Certificates;
using Autofac.Extensions.DependencyInjection;
using Squid.Api.Setup;
using Squid.Core.Persistence.Db;
using Squid.Core.Settings.Logging;
using Squid.Core.Settings.SelfCert;
using Squid.Core.Settings.Setup;
using Squid.Core.Settings.System;
using Squid.Core.Setup;
using Squid.Core.Setup.Configuration;
using Squid.Core.Setup.Embedded;

namespace Squid.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        var configuration = BuildConfiguration();
        var application = new SerilogApplicationSetting(configuration).Value;

        Log.Logger = CreateLogger(configuration, application);

        try
        {
            Log.Information("Configuring api host ({ApplicationContext})...", application);

            await using var embedded = new EmbeddedServiceHost(new ProcessRunner(), new DataDirectorySetting(configuration).Value);

            configuration = BootstrapDataDirectory(configuration);

            await FirstRunSetup.EnsureCompletedAsync(args, configuration, embedded);

            configuration = BuildConfiguration();

            await embedded.StartConfiguredAsync(new EmbeddedServicesSetting(configuration), CancellationToken.None);

            new DbUpRunner(new SquidConnectionString(configuration).Value).Run();

            var webHost = CreateHostBuilder(args, configuration).Build();

            Log.Information("Starting api host ({ApplicationContext})...", application);

            await webHost.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Program terminated unexpectedly ({ApplicationContext})!", application);

            Environment.ExitCode = 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .AddDataDirectoryFile()
            .Build();

    /// <summary>Self-contained installs: fill in missing per-install secrets, then re-read so they apply.</summary>
    private static IConfiguration BootstrapDataDirectory(IConfiguration configuration) => DataDirectoryBootstrapper.Run(configuration) ? BuildConfiguration() : configuration;

    private static Serilog.Core.Logger CreateLogger(IConfiguration configuration, string application)
    {
        var apiKey = new SerilogApiKeySetting(configuration).Value;
        var serverUrl = new SerilogServerUrlSetting(configuration).Value;

        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .WriteTo.Console();

        if (!string.IsNullOrWhiteSpace(serverUrl)) logger.WriteTo.Seq(serverUrl, apiKey: apiKey);

        return logger.CreateLogger();
    }

    private static IHostBuilder CreateHostBuilder(string[] args, IConfiguration configuration) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureLogging(l => l.AddSerilog(Log.Logger))
            .ConfigureAppConfiguration(c => c.AddDataDirectoryFile())
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
