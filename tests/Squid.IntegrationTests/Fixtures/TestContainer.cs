using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using Squid.Core;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Settings.SelfCert;

namespace Squid.IntegrationTests.Fixtures;

public interface ITestContainer : IDisposable, IAsyncDisposable
{
    ILifetimeScope LifetimeScope { get; }
    SquidDbContext DbContext { get; }
    T Resolve<T>();
    T Resolve<T>(Action<ContainerBuilder> register);
}

public class TestContainer : ITestContainer
{
    public ILifetimeScope LifetimeScope { get; }
    public SquidDbContext DbContext => LifetimeScope.Resolve<SquidDbContext>();

    private static readonly ILogger Logger;
    private static int _instanceCounter;

    static TestContainer()
    {
        Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("ApplicationContext", "IntegrationTests")
            .WriteTo.Console()
            .CreateLogger();
        Log.Logger = Logger;
    }

    public TestContainer(string? testName = null)
    {
        var instanceNum = Interlocked.Increment(ref _instanceCounter);
        var containerBuilder = new ContainerBuilder();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", false, true)
            .AddEnvironmentVariables()
            .Build();

        var originalSetting = configuration.GetSection("SquidStore").Get<SquidStoreSetting>() ?? new SquidStoreSetting();
        var storeSetting = new SquidStoreSetting
        {
            Type = SquidStoreSetting.SquidStoreType.Postgres,
            Postgres = CreateIsolatedPostgresSetting(originalSetting.Postgres, testName ?? $"test{instanceNum}")
        };

        var selfCertSetting = configuration.GetSection("SelfCert").Get<SelfCertSetting>() ?? new SelfCertSetting
        {
            Base64 = Environment.GetEnvironmentVariable("HALIBUT_CERT_BASE64"),
            Password = Environment.GetEnvironmentVariable("HALIBUT_CERT_PASSWORD") ?? string.Empty
        };

        ApplicationStartup.Initialize(containerBuilder, storeSetting, Logger, configuration, selfCertSetting);

        LifetimeScope = containerBuilder.Build();
    }

    private static string DatabaseName(string suffix) => $"squid_integrationtests_{suffix.ToLowerInvariant()}";

    private static ConnectionSetting CreateIsolatedPostgresSetting(ConnectionSetting? original, string suffix)
    {
        var connectionString = original?.ConnectionString ?? 
            "Host=localhost;Port=5432;Database=squid;Username=squid;Password=squid";
        
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = DatabaseName(suffix)
        };

        return new ConnectionSetting
        {
            ConnectionString = builder.ToString(),
            Version = original?.Version ?? "15"
        };
    }

    public T Resolve<T>() => LifetimeScope.Resolve<T>();

    public T Resolve<T>(Action<ContainerBuilder> register)
    {
        using var scope = LifetimeScope.BeginLifetimeScope(register);
        return scope.Resolve<T>();
    }

    public void Dispose()
    {
        CleanupAsync().Wait();
        LifetimeScope.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        await LifetimeScope.DisposeAsync();
    }

    private async Task CleanupAsync()
    {
        try
        {
            var dbName = DbContext.Database.GetDbConnection().Database;
            if (dbName.StartsWith("squid_integrationtests_", StringComparison.OrdinalIgnoreCase))
            {
                await DbContext.Database.EnsureDeletedAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to cleanup test database");
        }
    }
}
