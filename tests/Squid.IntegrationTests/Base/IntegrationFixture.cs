using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using Squid.Core.DbUpFiles;
using Squid.Core.Settings;
using Squid.Core.Settings.SelfCert;

namespace Squid.IntegrationTests.Base;

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

        var connectionString = new SquidConnectionString(configuration).Value;

        if (!string.IsNullOrEmpty(connectionString))
        {
            connectionString = CreateIsolatedConnectionString(connectionString);
            new DbUpRunner(connectionString).Run(nameof(Core.DbUpFiles), typeof(DbUpRunner).Assembly);
        }

        containerBuilder.RegisterModule(new SquidModule(logger, configuration));

        // Override SelfCertSetting with env var fallback for CI environments
        var selfCertSetting = configuration.GetSection("SelfCert").Get<SelfCertSetting>();
        if (string.IsNullOrEmpty(selfCertSetting?.Base64))
        {
            containerBuilder.RegisterInstance(new SelfCertSetting
            {
                Base64 = Environment.GetEnvironmentVariable("HALIBUT_CERT_BASE64"),
                Password = Environment.GetEnvironmentVariable("HALIBUT_CERT_PASSWORD") ?? string.Empty
            }).AsSelf().SingleInstance();
        }

        LifetimeScope = containerBuilder.Build();
    }

    private static string DatabaseName => $"squid_integrationtests_{typeof(TTestClass).Name.ToLowerInvariant()}";

    private static string CreateIsolatedConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = DatabaseName
        };

        return builder.ToString();
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var context = LifetimeScope.Resolve<SquidDbContext>();

        var dbName = context.Database.GetDbConnection().Database;

        if (!dbName.StartsWith("squid_integrationtests_", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Skipping database deletion: {DatabaseName} is not a test database", dbName);
            return;
        }

        await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
    }
}
