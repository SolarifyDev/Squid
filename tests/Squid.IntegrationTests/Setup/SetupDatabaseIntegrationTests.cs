using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Squid.Core.Settings.Setup;
using Squid.Core.Setup.Configuration;
using Squid.Core.Setup.Embedded;
using Squid.Core.Setup.Install;
using Squid.Message.Models.Setup;

namespace Squid.IntegrationTests.Setup;

/// <summary>
/// First-run setup against a real PostgreSQL: connection probing, a full External-mode install
/// (database created, schema migrated, configuration persisted), and the built-in cluster's real
/// initdb / start / stop when PostgreSQL server binaries are installed on this machine.
/// </summary>
public sealed class SetupDatabaseIntegrationTests : IDisposable
{
    // Same Redis the CI `redis` service exposes (see UpgradeDispatchLockReconcilerIntegrationTests).
    private static readonly string RedisConnectionString = Environment.GetEnvironmentVariable("SQUID_TEST_REDIS_CONN_STRING") ?? "127.0.0.1:6379,abortConnect=False";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"squid-setup-it-{Guid.NewGuid():N}");
    private readonly string _databaseName = $"squid_setup_it_{Guid.NewGuid():N}";
    private readonly NpgsqlConnectionStringBuilder _server;

    public SetupDatabaseIntegrationTests()
    {
        Directory.CreateDirectory(_dataDirectory);

        var baseConnectionString = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build()["SquidStore:ConnectionString"];

        _server = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = _databaseName, Pooling = false };
    }

    [Fact]
    public async Task Probe_MissingDatabase_ReportsItWillBeCreated()
    {
        var result = await DatabaseConnectionTester.TestAsync(_server.ConnectionString, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.DatabaseExists.ShouldBeFalse();
        result.CanCreateDatabase.ShouldBeTrue();
        result.ServerVersion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Probe_WrongPassword_FailsWithServerMessage()
    {
        var wrong = new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Password = "definitely-wrong" };

        var result = await DatabaseConnectionTester.TestAsync(wrong.ConnectionString, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("password");
    }

    [Fact]
    public async Task Install_ExternalDatabase_CreatesMigratesAndPersists()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = _dataDirectory }).Build();
        var tracker = new SetupProgressTracker();
        var request = BuildRequest(new SetupDatabaseChoice { Mode = SetupServiceMode.External, ConnectionString = _server.ConnectionString, CreateIfMissing = true });

        tracker.TryBegin(SetupInstaller.Steps);

        await new SetupInstaller(new EmbeddedServiceHost(new ProcessRunner(), _dataDirectory), configuration, tracker).InstallAsync(request, CancellationToken.None);

        tracker.Snapshot().State.ShouldBe(SetupInstallState.Completed);
        (await CountTablesAsync(_server.ConnectionString)).ShouldBeGreaterThan(10);

        var configFile = Path.Combine(_dataDirectory, DataDirectorySetting.ConfigurationFileName);

        SquidConfigurationFile.GetValue(configFile, "SquidStore:ConnectionString").ShouldBe(_server.ConnectionString);
        SquidConfigurationFile.GetValue(configFile, "Setup:Admin:UserName").ShouldBe("ops");
        SquidConfigurationFile.GetValue(configFile, "Setup:Admin:PasswordHash").ShouldNotBeNullOrEmpty();
        File.ReadAllText(configFile).ShouldNotContain("longenough");
    }

    [Fact]
    public async Task Install_MissingDatabaseWithoutCreate_FailsBeforeWritingConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = _dataDirectory }).Build();
        var request = BuildRequest(new SetupDatabaseChoice { Mode = SetupServiceMode.External, ConnectionString = _server.ConnectionString, CreateIfMissing = false });

        await Should.ThrowAsync<InvalidOperationException>(() => new SetupInstaller(new EmbeddedServiceHost(new ProcessRunner(), _dataDirectory), configuration, new SetupProgressTracker()).InstallAsync(request, CancellationToken.None));

        File.Exists(Path.Combine(_dataDirectory, DataDirectorySetting.ConfigurationFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task BuiltinPostgres_InitialisesStartsAcceptsLoginAndStops()
    {
        if (!EmbeddedServiceHost.IsPostgresAvailable(new EmbeddedPostgresSettings())) return;

        var settings = new EmbeddedPostgresSettings { Port = FreePort(), Password = "it-password" };
        var connectionString = string.Empty;

        await using (var host = new EmbeddedServiceHost(new ProcessRunner(), _dataDirectory))
        {
            connectionString = await host.StartPostgresAsync(settings, CancellationToken.None);

            var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };

            (await ScalarAsync(maintenance.ConnectionString, "SELECT current_user")).ShouldBe("squid");
            (await ScalarAsync(maintenance.ConnectionString, "SHOW listen_addresses")).ShouldBe("127.0.0.1");
        }

        File.Exists(Path.Combine(_dataDirectory, EmbeddedPostgres.DataFolderName, "postmaster.pid")).ShouldBeFalse("disposing the host must stop the cluster (pg_ctl stop)");
    }

    public void Dispose()
    {
        DropDatabase();

        try { Directory.Delete(_dataDirectory, true); } catch (IOException) { }
    }

    private static SetupInstallRequest BuildRequest(SetupDatabaseChoice database) => new()
    {
        Database = database,
        Redis = new SetupRedisChoice { Mode = SetupServiceMode.External, ConnectionString = RedisConnectionString },
        Admin = new SetupAdminAccount { UserName = "ops", Password = "longenough" },
        Server = new SetupServerUrls { ExternalUrl = "http://localhost:8080" }
    };

    private static async Task<long> CountTablesAsync(string connectionString) => (long)await ScalarObjectAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'");

    private static async Task<string> ScalarAsync(string connectionString, string sql) => (await ScalarObjectAsync(connectionString, sql))?.ToString();

    private static async Task<object> ScalarObjectAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return await command.ExecuteScalarAsync();
    }

    private void DropDatabase()
    {
        // DROP DATABASE checkpoints the server; under a full parallel suite that can exceed the 30s default.
        const int dropTimeoutSeconds = 180;

        NpgsqlConnection.ClearAllPools();

        var maintenance = new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Database = "postgres" };

        using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection) { CommandTimeout = dropTimeoutSeconds };
        command.ExecuteNonQuery();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
