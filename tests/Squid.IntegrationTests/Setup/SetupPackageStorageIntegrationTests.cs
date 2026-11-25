using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
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

namespace Squid.IntegrationTests.Setup;

/// <summary>
/// The installer's package storage step against a real PostgreSQL that the installer itself migrated, so the row it updates is the one the
/// migration seeded.
/// </summary>
public sealed class SetupPackageStorageIntegrationTests : IDisposable
{
    private const string SecretKind = "SecretProbe";
    private const string Token = "setup-token-PLAINTEXT-VALUE";

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
        rows[0].ShouldBe(new StoreRow("Local disk", "FileSystem", "{}", null, true));
    }

    [Fact]
    public async Task EmptyLocalDiskChoice_IsTheDefault_SoNothingIsWritten()
    {
        await InstallAsync(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Root"] = "" } });

        (await ReadRowsAsync()).Single().ShouldBe(new StoreRow("Local disk", "FileSystem", "{}", null, true));
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
    public async Task SecretBearingKind_IsStoredAsCiphertext_AndDecryptsWithTheServersKey()
    {
        await InstallAsync(new SetupPackageStorageChoice { Kind = SecretKind, Properties = new() { ["Endpoint"] = "https://store.example" }, Secrets = new() { ["Token"] = Token } });

        var row = (await ReadRowsAsync()).Single();

        row.Kind.ShouldBe(SecretKind);
        row.IsDefault.ShouldBeTrue();
        row.Secret.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        row.Secret.ShouldNotContain(Token);
        row.Properties.ShouldNotContain(Token);

        var protector = new AtRestSecretProtector(new VariableEncryptionService(new SecuritySetting(_configuration)));
        var decrypted = PackageStoreConfigurationMapper.ParseSettings(await protector.UnprotectAsync(row.Secret, PackageStoreDataProvider.SecretKdfScope));

        decrypted["Token"].ShouldBe(Token);
    }

    [Fact]
    public async Task UnknownKind_FailsTheInstall_AndWritesNoConfiguration()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => InstallAsync(new SetupPackageStorageChoice { Kind = "Nope" }));

        File.Exists(Path.Combine(_dataDirectory, DataDirectorySetting.ConfigurationFileName)).ShouldBeFalse();
    }

    public void Dispose()
    {
        using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Database = "postgres" }.ConnectionString))
        {
            connection.Open();

            using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
            command.ExecuteNonQuery();
        }

        try { Directory.Delete(_dataDirectory, true); } catch (IOException) { }
    }

    private Task InstallAsync(SetupPackageStorageChoice storage)
    {
        var request = new SetupInstallRequest
        {
            Database = new SetupDatabaseChoice { Mode = SetupServiceMode.External, ConnectionString = _server.ConnectionString, CreateIfMissing = true },
            Redis = new SetupRedisChoice { Mode = SetupServiceMode.External, ConnectionString = RedisConnectionString },
            Admin = new SetupAdminAccount { UserName = "ops", Password = "longenough" },
            Server = new SetupServerUrls { ExternalUrl = "http://localhost:8080" },
            PackageStorage = storage
        };
        var providers = new IPackageBlobStoreProvider[] { new FileSystemPackageBlobStoreProvider(new DataDirectorySetting(_configuration)), new SecretProbeProvider() };

        return new SetupInstaller(new EmbeddedServiceHost(new ProcessRunner(), _dataDirectory), _configuration, new SetupProgressTracker(), providers).InstallAsync(request, CancellationToken.None);
    }

    private async Task<List<StoreRow>> ReadRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_server.ConnectionString);

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

        public IPackageBlobStore Create(PackageBlobStoreConfiguration configuration) => throw new NotSupportedException();

        public Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct) => Task.FromResult(new PackageBlobStoreTestResult { Success = true, Message = "ok" });
    }
}
