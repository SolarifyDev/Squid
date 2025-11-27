using System.IO;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using Serilog.Sinks.SystemConsole;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Postgres;
using Squid.Message;
using Squid.Core.Settings.SelfCert;

namespace Squid.IntegrationTests;

public interface IIntegrationFixture
{
    ILifetimeScope LifetimeScope { get; }
}

public class IntegrationFixture<TTestClass> : IAsyncLifetime, IIntegrationFixture
{
    public ILifetimeScope LifetimeScope { get; }

    public IntegrationFixture()
    {
        var containerBuilder = new ContainerBuilder();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", false, true)
            .Build();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("ApplicationContext", "Squid")
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateLogger();

        Log.Logger = logger;

        var storeSetting = configuration.GetSection("SquidStore").Get<SquidStoreSetting>();

        if (storeSetting?.Postgres != null)
        {
            storeSetting.Postgres = CreateIsolatedPostgresSetting(storeSetting.Postgres);
        }

        var selfCertSetting = configuration.GetSection("SelfCert").Get<SelfCertSetting>() ?? new SelfCertSetting
        {
            Base64 = Environment.GetEnvironmentVariable("HALIBUT_CERT_BASE64"),
            Password = Environment.GetEnvironmentVariable("HALIBUT_CERT_PASSWORD") ?? string.Empty
        };

        ApplicationStartup.Initialize(
            containerBuilder,
            storeSetting!,
            logger,
            new IntegrationTestUser(),
            configuration,
            selfCertSetting);

        LifetimeScope = containerBuilder.Build();
    }

    private static string DatabaseName => $"squid_integrationtests_{typeof(TTestClass).Name.ToLowerInvariant()}";

    private static PostgresSetting CreateIsolatedPostgresSetting(PostgresSetting original)
    {
        var builder = new NpgsqlConnectionStringBuilder(original.ConnectionString)
        {
            Database = DatabaseName
        };

        return new PostgresSetting
        {
            ConnectionString = builder.ToString(),
            Version = original.Version
        };
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        var context = LifetimeScope.Resolve<SquidDbContext>();
        return context.Database.EnsureDeletedAsync();
    }
}
