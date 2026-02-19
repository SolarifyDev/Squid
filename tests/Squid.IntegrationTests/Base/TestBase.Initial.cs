using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using Squid.Core.DbUpFiles;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings;
using Squid.Core.Settings.SelfCert;

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

        new DbUpRunner(isolatedConnectionString).Run(nameof(Core.DbUpFiles), typeof(DbUpRunner).Assembly);
        ShouldRunDbUpDatabases[_databaseName] = true;
    }

    private void ClearDatabaseRecords()
    {
        using var scope = CurrentScope.BeginLifetimeScope();
        var context = scope.Resolve<SquidDbContext>();

        var tables = new[]
        {
            "action_channels",
            "action_environments",
            "action_machine_roles",
            "activity_log",
            "channel",
            "deployment",
            "deployment_account",
            "deployment_action",
            "deployment_action_property",
            "deployment_completion",
            "deployment_environment",
            "deployment_process",
            "deployment_process_snapshot",
            "deployment_step",
            "deployment_step_property",
            "environment",
            "external_feed",
            "library_variable_set",
            "lifecycle",
            "machine",
            "machine_policy",
            "phase",
            "project",
            "release",
            "release_selected_package",
            "retention_policy",
            "server_task",
            "server_task_log",
            "space",
            "variable",
            "variable_scope",
            "variable_set",
            "variable_set_snapshot"
        };

        var tableList = string.Join(", ", tables);

        // Table names are hardcoded constants, not user input — safe to concatenate
        #pragma warning disable EF1002
        context.Database.ExecuteSqlRaw($"TRUNCATE TABLE {tableList} RESTART IDENTITY CASCADE");
        #pragma warning restore EF1002
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
