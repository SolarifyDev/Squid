using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Core.Settings.Setup;
using Squid.Core.Setup.Embedded;
using Squid.Core.Setup.Install;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;
using Squid.Message.Models.Setup;
using Squid.IntegrationTests.Helpers;

namespace Squid.IntegrationTests.Setup;

/// <summary>
/// The installer's package storage step against a real PostgreSQL that the installer itself migrated, so the row it updates is the one the
/// migration seeded. A shared <see cref="SetupSchemaOrigin"/> stands for retries within one setup session.
/// </summary>
public sealed class SetupPackageStorageIntegrationTests : IDisposable
{
    private const string SecretKind = "SecretProbe";
    private const string Token = "setup-token-PLAINTEXT-VALUE";
    private const string SeededProperties = "{}";

    private static readonly string RedisConnectionString = Environment.GetEnvironmentVariable("SQUID_TEST_REDIS_CONN_STRING") ?? "127.0.0.1:6379,abortConnect=False";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"squid-setup-pkg-it-{Guid.NewGuid():N}");
    private readonly string _databaseName = $"squid_setup_pkg_it_{Guid.NewGuid():N}";
    private readonly IConfiguration _configuration;
    private readonly NpgsqlConnectionStringBuilder _server;

    public SetupPackageStorageIntegrationTests()
    {
        Directory.CreateDirectory(_dataDirectory);

        var appsettings = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build();

        _server = new NpgsqlConnectionStringBuilder(appsettings["SquidStore:ConnectionString"]) { Database = _databaseName, Pooling = false };
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = _dataDirectory, ["Security:VariableEncryption:MasterKey"] = appsettings["Security:VariableEncryption:MasterKey"] }).Build();
    }

    [Fact]
    public async Task NoPackageStorage_LeavesTheSeededDefaultAsTheMigrationWroteIt()
    {
        await InstallAsync(null);

        var rows = await ReadRowsAsync();

        rows.Count.ShouldBe(1);
        rows[0].ShouldBe(new StoreRow("Local disk", "FileSystem", SeededProperties, null, true));
    }

    [Fact]
    public async Task EmptyLocalDiskChoice_IsTheDefault_SoTheSeededValuesStay()
    {
        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "" } });

        (await ReadRowsAsync()).Single().ShouldBe(new StoreRow("Local disk", "FileSystem", SeededProperties, null, true));
    }

    [Fact]
    public async Task LocalDiskWithARoot_UpdatesTheSeededRow_NotANewOne()
    {
        var root = Path.Combine(_dataDirectory, "my-packages");

        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = root } });

        var rows = await ReadRowsAsync();

        rows.Count.ShouldBe(1);
        rows[0].Kind.ShouldBe("FileSystem");
        rows[0].IsDefault.ShouldBeTrue();
        rows[0].Secret.ShouldBeNull();
        JsonSerializer.Deserialize<Dictionary<string, string>>(rows[0].Properties)["Root"].ShouldBe(root);
    }

    [Fact]
    public async Task SecretBearingKind_IsStoredAsCiphertext_AndTheServersOwnDataProviderReadsItBack()
    {
        await InstallAsync(new SetupPackageStorageChoice { Kind = SecretKind, Properties = new() { ["Endpoint"] = "https://store.example" }, Secrets = new() { ["Token"] = Token } });

        var row = (await ReadRowsAsync()).Single();

        row.Kind.ShouldBe(SecretKind);
        row.IsDefault.ShouldBeTrue();
        row.Secret.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        row.Secret.ShouldNotContain(Token);
        row.Properties.ShouldNotContain(Token);

        var store = await ReadDefaultThroughServerDataProviderAsync();

        PackageStoreConfigurationMapper.ParseSettings(store.Secret)["Token"].ShouldBe(Token);
        PackageStoreConfigurationMapper.ParseSettings(store.Properties)["Endpoint"].ShouldBe("https://store.example");
    }

    [Fact]
    public async Task RetryWithTheDefaultChoice_ResetsWhatAnEarlierAttemptWrote()
    {
        var origin = new SetupSchemaOrigin();

        await InstallAsync(new SetupPackageStorageChoice { Kind = SecretKind, Properties = new() { ["Endpoint"] = "e" }, Secrets = new() { ["Token"] = Token } }, origin);
        (await ReadRowsAsync()).Single().Kind.ShouldBe(SecretKind);

        await InstallAsync(null, origin);

        (await ReadRowsAsync()).Single().ShouldBe(new StoreRow("Local disk", "FileSystem", SeededProperties, null, true));
    }

    [Fact]
    public async Task RetryWithAnotherRoot_ReplacesTheEarlierRoot()
    {
        var origin = new SetupSchemaOrigin();

        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/first" } }, origin);
        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/second" } }, origin);

        var rows = await ReadRowsAsync();

        rows.Count.ShouldBe(1);
        rows[0].Properties.ShouldContain("/tmp/second");
        rows[0].Properties.ShouldNotContain("/tmp/first");
    }

    [Fact]
    public async Task DatabaseThatAlreadyHasASquidSchema_KeepsItsPackageStorage_AndSaysSo()
    {
        new DbUpRunner(_server.ConnectionString).Run();
        await ExecuteAsync("UPDATE package_store SET name = 'Ops store', properties = '{\"Root\":\"/srv/pk\"}'");

        var tracker = await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/new" } });

        (await ReadRowsAsync()).Single().ShouldBe(new StoreRow("Ops store", "FileSystem", "{\"Root\":\"/srv/pk\"}", null, true));
        tracker.Snapshot().Steps.Single(step => step.Key == SetupInstaller.StepKeys.PackageStorage).Message.ShouldBe(SetupInstaller.ExistingPackageStorageKeptMessage);
    }

    [Fact]
    public async Task RetryAgainstAnExistingDatabase_AfterAnAttemptOnANewOne_KeepsTheExistingPackageStorage()
    {
        var origin = new SetupSchemaOrigin();
        var existingName = $"squid_setup_pkg_it_{Guid.NewGuid():N}";
        var existing = new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Database = existingName }.ConnectionString;

        try
        {
            await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/first" } }, origin);

            new DbUpRunner(existing).Run();
            await ExecuteAsync("UPDATE package_store SET name = 'Ops store', properties = '{\"Root\":\"/srv/pk\"}'", existing);

            await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/new" } }, origin, existing);

            (await ReadRowsAsync(existing)).Single().ShouldBe(new StoreRow("Ops store", "FileSystem", "{\"Root\":\"/srv/pk\"}", null, true));
        }
        finally
        {
            ThrowawayDatabase.Drop(existing, existingName);
        }
    }

    [Fact]
    public async Task SameDatabaseRetried_WithOtherConnectionTuning_StillCountsAsFresh()
    {
        var origin = new SetupSchemaOrigin();
        var tuned = new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Timeout = 25 }.ConnectionString;

        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/first" } }, origin);
        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/second" } }, origin, tuned);

        (await ReadRowsAsync()).Single().Properties.ShouldContain("/tmp/second");
    }

    [Fact]
    public async Task SchemaFromBeforePackageStorageExisted_IsNotTreatedAsFresh()
    {
        await ExecuteAsync($"CREATE DATABASE \"{_databaseName}\"", new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Database = "postgres" }.ConnectionString);
        await ExecuteAsync("CREATE TABLE schemaversions (schemaversionsid serial PRIMARY KEY, scriptname text NOT NULL, applied timestamp NOT NULL)");

        var origin = new SetupSchemaOrigin();

        await origin.RecordAsync(_server.ConnectionString, CancellationToken.None);

        origin.WasFresh(_server.ConnectionString).ShouldBeFalse();
    }

    [Fact]
    public async Task OnlyTheDefaultRowIsChanged_NotOtherStores()
    {
        var origin = new SetupSchemaOrigin();

        await InstallAsync(null, origin);
        await ExecuteAsync("INSERT INTO package_store (name, kind, properties, is_default) VALUES ('Other', 'FileSystem', '{\"Root\":\"/other\"}', false)");

        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/chosen" } }, origin);

        var rows = await ReadRowsAsync();

        rows.Count.ShouldBe(2);
        rows.Single(r => r.Name == "Other").ShouldBe(new StoreRow("Other", "FileSystem", "{\"Root\":\"/other\"}", null, false));
        rows.Single(r => r.IsDefault).Properties.ShouldContain("/tmp/chosen");
    }

    [Fact]
    public async Task NoDefaultRow_FailsWithAClearMessage_AndChangesNothing()
    {
        var origin = new SetupSchemaOrigin();

        await InstallAsync(null, origin);
        await ExecuteAsync("UPDATE package_store SET is_default = false");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "/tmp/x" } }, origin));

        ex.Message.ShouldContain("no default package store");
        (await ReadRowsAsync()).Single().Properties.ShouldBe(SeededProperties);
    }

    [Fact]
    public async Task AnotherStoreWithTheProvidersName_FailsWithAClearMessage_NotARawPostgresError()
    {
        var origin = new SetupSchemaOrigin();

        await InstallAsync(null, origin);
        await ExecuteAsync("INSERT INTO package_store (name, kind, properties, is_default) VALUES ('Secret probe', 'FileSystem', '{}', false)");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => InstallAsync(new SetupPackageStorageChoice { Kind = SecretKind, Properties = new() { ["Endpoint"] = "e" }, Secrets = new() { ["Token"] = Token } }, origin));

        ex.ShouldNotBeOfType<PostgresException>();
        ex.Message.ShouldContain("already named \"Secret probe\"");
    }

    [Fact]
    public async Task UnknownKind_FailsTheInstall_AndWritesNoConfiguration()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => InstallAsync(new SetupPackageStorageChoice { Kind = "Nope" }));

        File.Exists(Path.Combine(_dataDirectory, DataDirectorySetting.ConfigurationFileName)).ShouldBeFalse();
    }

    public void Dispose()
    {
        ThrowawayDatabase.Drop(_server.ConnectionString, _databaseName);

        try { Directory.Delete(_dataDirectory, true); } catch (IOException) { }
    }

    private async Task<SetupProgressTracker> InstallAsync(SetupPackageStorageChoice storage, SetupSchemaOrigin origin = null, string connectionString = null)
    {
        var request = new SetupInstallRequest
        {
            Database = new SetupDatabaseChoice { Mode = SetupServiceMode.External, ConnectionString = connectionString ?? _server.ConnectionString, CreateIfMissing = true },
            Redis = new SetupRedisChoice { Mode = SetupServiceMode.External, ConnectionString = RedisConnectionString },
            Admin = new SetupAdminAccount { UserName = "ops", Password = "longenough" },
            Server = new SetupServerUrls { ExternalUrl = "http://localhost:8080" },
            PackageStorage = storage
        };
        var providers = new IPackageBlobStoreProvider[] { new FileSystemPackageBlobStoreProvider(new DataDirectorySetting(_configuration)), new SecretProbeProvider() };
        var tracker = new SetupProgressTracker();

        tracker.TryBegin(SetupInstaller.Steps);

        await new SetupInstaller(new EmbeddedServiceHost(new ProcessRunner(), _dataDirectory), _configuration, tracker, providers, origin).InstallAsync(request, CancellationToken.None);

        return tracker;
    }

    private async Task ExecuteAsync(string sql, string connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? _server.ConnectionString);

        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync();
    }

    // The server's own data provider (decrypting protector included), over a context on the installed database.
    private async Task<PackageStore> ReadDefaultThroughServerDataProviderAsync()
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>().UseNpgsql(_server.ConnectionString).UseSnakeCaseNamingConvention().Options;

        await using var context = new SquidDbContext(options);

        var protector = new AtRestSecretProtector(new VariableEncryptionService(new SecuritySetting(_configuration)));

        return await new PackageStoreDataProvider(context, new EfRepository(context), protector).GetDefaultAsync(CancellationToken.None);
    }

    private async Task<List<StoreRow>> ReadRowsAsync(string connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? _server.ConnectionString);

        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT name, kind, properties, secret, is_default FROM package_store ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<StoreRow>();

        while (await reader.ReadAsync())
            rows.Add(new StoreRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));

        return rows;
    }

    private sealed record StoreRow(string Name, string Kind, string Properties, string Secret, bool IsDefault);

    /// <summary>A remote-style kind with a secret field, so secret handling is exercised without a real remote store.</summary>
    private sealed class SecretProbeProvider : IPackageBlobStoreProvider
    {
        public string Kind => SecretKind;

        public PackageStoreProviderDescriptor Describe() => new()
        {
            Kind = Kind, DisplayName = "Secret probe", Description = "test",
            Fields =
            [
                new PackageStoreFieldDescriptor { Key = "Endpoint", Label = "Endpoint", Kind = PackageStoreFieldKind.Text, Required = true },
                new PackageStoreFieldDescriptor { Key = "Token", Label = "Token", Kind = PackageStoreFieldKind.Secret, Required = true }
            ]
        };

        public IReadOnlyList<string> ValidateConfiguration(PackageBlobStoreConfiguration configuration) => [];

        public IPackageBlobStore Create(PackageBlobStoreConfiguration configuration) => throw new NotSupportedException();

        public Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct) => Task.FromResult(new PackageBlobStoreTestResult { Success = true, Message = "ok" });
    }
}
