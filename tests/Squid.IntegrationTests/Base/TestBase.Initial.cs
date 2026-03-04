using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.SelfCert;
using Squid.Core.Settings.System;

namespace Squid.IntegrationTests.Base;

public partial class TestBase
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        ClearDatabaseRecords();
    }

    private void RegisterBaseContainer(ContainerBuilder containerBuilder)
    {
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

        var isolatedConnectionString = GetIsolatedConnectionString(configuration);

        containerBuilder.RegisterInstance(configuration).As<IConfiguration>().SingleInstance();
        containerBuilder.RegisterModule(new SquidModule(logger, configuration));

        containerBuilder.Register(_ =>
            {
                var dbContextBuilder = new DbContextOptionsBuilder<SquidDbContext>();
                dbContextBuilder.UseNpgsql(isolatedConnectionString).UseSnakeCaseNamingConvention();
                return new SquidDbContext(dbContextBuilder.Options);
            })
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        var selfCertSetting = configuration.GetSection("SelfCert").Get<SelfCertSetting>();

        if (string.IsNullOrEmpty(selfCertSetting?.Base64))
        {
            containerBuilder.RegisterInstance(new SelfCertSetting
            {
                Base64 = Environment.GetEnvironmentVariable("HALIBUT_CERT_BASE64"),
                Password = Environment.GetEnvironmentVariable("HALIBUT_CERT_PASSWORD") ?? string.Empty
            }).AsSelf().SingleInstance();
        }

        containerBuilder.RegisterType<MockSquidBackgroundJobClient>()
            .As<ISquidBackgroundJobClient>()
            .InstancePerLifetimeScope();
    }

    private void RunDbUpIfRequired()
    {
        if (ShouldRunDbUpDatabases.TryGetValue(_databaseName, out _))
            return;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", false, true)
            .Build();

        var isolatedConnectionString = GetIsolatedConnectionString(configuration);

        new DbUpRunner(isolatedConnectionString).Run();
        ShouldRunDbUpDatabases[_databaseName] = true;
    }

    private void ClearDatabaseRecords()
    {
        using var scope = CurrentScope.BeginLifetimeScope();
        var context = scope.Resolve<SquidDbContext>();

        using var connection = context.Database.GetDbConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT string_agg(quote_ident(tablename), ', ')
            FROM pg_tables
            WHERE schemaname = 'public' AND tablename <> 'schemaversions'
            """;

        var tableList = command.ExecuteScalar() as string;

        if (string.IsNullOrEmpty(tableList)) return;

        using var truncateCommand = connection.CreateCommand();
        truncateCommand.CommandText = $"TRUNCATE TABLE {tableList} RESTART IDENTITY CASCADE";
        truncateCommand.ExecuteNonQuery();
    }

    private string GetIsolatedConnectionString(IConfiguration configuration)
    {
        var baseConnectionString = new SquidConnectionString(configuration).Value;

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = _databaseName
        };

        return builder.ToString();
    }
}
