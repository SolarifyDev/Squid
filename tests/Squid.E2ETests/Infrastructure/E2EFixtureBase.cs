using Autofac;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using Squid.Core;
using Squid.Core.Persistence.Db;
using Squid.Core.Settings.SelfCert;
using Xunit;

namespace Squid.E2ETests.Infrastructure;

public class E2EFixtureBase<TTestClass> : IAsyncLifetime
{
    public ILifetimeScope LifetimeScope { get; private set; }

    private string _databaseName;
    private string _connectionString;

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["SquidStore:ConnectionString"] = GetBaseConnectionString(),
                ["SelfCert:Base64"] = "",
                ["SelfCert:Password"] = "",
                ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("ApplicationContext", "Squid.E2E")
            .WriteTo.Console()
            .CreateLogger();

        Log.Logger = logger;

        _connectionString = CreateIsolatedConnectionString(GetBaseConnectionString());
        new DbUpRunner(_connectionString).Run();

        configuration["SquidStore:ConnectionString"] = _connectionString;

        var containerBuilder = new ContainerBuilder();
        containerBuilder.RegisterInstance(configuration).As<IConfiguration>().SingleInstance();
        containerBuilder.RegisterModule(new SquidModule(logger, configuration));

        containerBuilder.RegisterInstance(new SelfCertSetting
        {
            Base64 = Environment.GetEnvironmentVariable("HALIBUT_CERT_BASE64")
                     ?? Convert.ToBase64String(CreateSelfSignedCertBytes()),
            Password = Environment.GetEnvironmentVariable("HALIBUT_CERT_PASSWORD") ?? string.Empty
        }).AsSelf().SingleInstance();

        RegisterOverrides(containerBuilder, configuration);

        LifetimeScope = containerBuilder.Build();

        await OnInitializedAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await OnDisposingAsync().ConfigureAwait(false);

        if (LifetimeScope is IDisposable disposable)
            disposable.Dispose();

        await DropDatabaseAsync().ConfigureAwait(false);
    }

    protected virtual void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration) { }

    protected virtual Task OnInitializedAsync() => Task.CompletedTask;

    protected virtual Task OnDisposingAsync() => Task.CompletedTask;

    public async Task Run<T>(Func<T, Task> action)
    {
        await using var scope = LifetimeScope.BeginLifetimeScope();
        await action(scope.Resolve<T>()).ConfigureAwait(false);
    }

    public async Task Run<T1, T2>(Func<T1, T2, Task> action)
    {
        await using var scope = LifetimeScope.BeginLifetimeScope();
        await action(scope.Resolve<T1>(), scope.Resolve<T2>()).ConfigureAwait(false);
    }

    public async Task<TR> Run<T, TR>(Func<T, Task<TR>> action)
    {
        await using var scope = LifetimeScope.BeginLifetimeScope();
        return await action(scope.Resolve<T>()).ConfigureAwait(false);
    }

    private static string GetBaseConnectionString()
    {
        var envCs = Environment.GetEnvironmentVariable("SQUID_TEST_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(envCs)) return envCs;

        return "Host=localhost;Port=5432;Database=squid;Username=squid;Password=squid";
    }

    private string CreateIsolatedConnectionString(string baseConnectionString)
    {
        _databaseName = $"squid_e2e_{typeof(TTestClass).Name.ToLowerInvariant()}";

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = _databaseName
        };

        return builder.ToString();
    }

    private async Task DropDatabaseAsync()
    {
        if (string.IsNullOrEmpty(_databaseName)) return;

        if (!_databaseName.StartsWith("squid_e2e_", StringComparison.OrdinalIgnoreCase)) return;

        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = "postgres"
        };

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync().ConfigureAwait(false);

        await using var terminateCmd = conn.CreateCommand();
        terminateCmd.CommandText = $"""
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = '{_databaseName}'
            AND pid <> pg_backend_pid()
            """;
        await terminateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        await using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\"";
        await dropCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static byte[] CreateSelfSignedCertBytes()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);

        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=squid-e2e", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));

        return cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, string.Empty);
    }
}
