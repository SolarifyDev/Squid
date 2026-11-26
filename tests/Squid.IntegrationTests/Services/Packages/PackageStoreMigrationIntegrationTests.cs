using Microsoft.Extensions.Configuration;
using Npgsql;
using Squid.Core.Persistence.Db;
using Squid.Core.Settings.System;
using Squid.IntegrationTests.Helpers;

namespace Squid.IntegrationTests.Services.Packages;

/// <summary>
/// Runs the real DbUp migrations against a throw-away database of its own (the shared test databases are truncated after every test, which would
/// erase the row the migration seeds) and checks what an upgraded install ends up with.
/// </summary>
public sealed class PackageStoreMigrationIntegrationTests : IDisposable
{
    private const string ScriptName = "20261126_add_package_store.sql";

    private readonly string _databaseName = $"squid_it_pkgstore_migration_{Guid.NewGuid():N}";
    private readonly string _connectionString;

    public PackageStoreMigrationIntegrationTests()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json", false, true).Build();
        var builder = new NpgsqlConnectionStringBuilder(new SquidConnectionString(configuration).Value) { Database = _databaseName, Pooling = false };

        _connectionString = builder.ToString();

        MigrateWithRetry();
    }

    // The suite runs hundreds of database-creating classes at once; CREATE DATABASE or a connection can time out under that load, which says nothing about the migration.
    private void MigrateWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                new DbUpRunner(_connectionString).Run();
                return;
            }
            catch (Exception) when (attempt < 3)
            {
                Thread.Sleep(2000);
            }
        }
    }

    public void Dispose()
    {
        ThrowawayDatabase.Drop(_connectionString, _databaseName);
    }

    [Fact]
    public async Task Migration_LeavesExactlyOneDefaultLocalDiskRow_WithNoSecret()
    {
        var rows = await QueryAsync("SELECT name, kind, properties, is_default, secret, created_by, last_tested_date FROM package_store");

        var row = rows.ShouldHaveSingleItem();

        row["name"].ShouldBe("Local disk");
        row["kind"].ShouldBe("FileSystem");
        row["properties"].ShouldBe("{}", "an empty root means: resolve at runtime");
        row["is_default"].ShouldBe(true);
        row["secret"].ShouldBeNull();
        row["last_tested_date"].ShouldBeNull();
    }

    [Fact]
    public async Task RunningTheScriptAgain_AddsNothing_AndKeepsTheExistingRowAsItWas()
    {
        await ExecuteAsync("UPDATE package_store SET properties = '{\"Root\":\"/srv/pk\"}'");

        await ExecuteAsync(await ReadScriptAsync());
        await ExecuteAsync(await ReadScriptAsync());

        var rows = await QueryAsync("SELECT properties FROM package_store");

        rows.ShouldHaveSingleItem()["properties"].ShouldBe("{\"Root\":\"/srv/pk\"}");
    }

    [Fact]
    public async Task TheSeed_IsSkipped_WhenTheOperatorAlreadyRenamedOrReplacedTheStore()
    {
        await ExecuteAsync("UPDATE package_store SET name = 'My packages'");
        await ExecuteAsync(await ReadScriptAsync());

        (await QueryAsync("SELECT name FROM package_store")).ShouldHaveSingleItem()["name"].ShouldBe("My packages");

        await ExecuteAsync("DELETE FROM package_store");
        await ExecuteAsync("INSERT INTO package_store (name, kind) VALUES ('Other', 'FileSystem')");
        await ExecuteAsync(await ReadScriptAsync());

        var rows = await QueryAsync("SELECT name, is_default FROM package_store");

        rows.ShouldHaveSingleItem()["name"].ShouldBe("Other");
        rows.Single()["is_default"].ShouldBe(false, "a table that already holds a store never gets a second default");
    }

    [Fact]
    public async Task TheDatabase_RefusesASecondDefaultRow_AndADuplicateName()
    {
        var second = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("INSERT INTO package_store (name, kind, is_default) VALUES ('Second', 'FileSystem', true)"));

        second.ConstraintName.ShouldBe("ux_package_store_single_default");

        var duplicate = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("INSERT INTO package_store (name, kind) VALUES ('Local disk', 'FileSystem')"));

        duplicate.ConstraintName.ShouldBe("ux_package_store_name_lower");

        await Should.NotThrowAsync(() => ExecuteAsync("INSERT INTO package_store (name, kind) VALUES ('Third', 'FileSystem'), ('Fourth', 'FileSystem')"));
    }

    private static async Task<string> ReadScriptAsync() => await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, ScriptName));

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<Dictionary<string, object>>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<Dictionary<string, object>>();

        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));

        return rows;
    }

    [Fact]
    public async Task Names_AreUniqueIgnoringCase_AfterTheSecondMigration()
    {
        var clash = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("INSERT INTO package_store (name, kind) VALUES ('LOCAL DISK', 'FileSystem')"));

        clash.ConstraintName.ShouldBe("ux_package_store_name_lower");
    }

    [Fact]
    public async Task TheCaseInsensitiveMigration_RenamesLaterDuplicates_SoItCanAlwaysBeApplied()
    {
        await ExecuteAsync("DROP INDEX ux_package_store_name_lower");
        await ExecuteAsync("INSERT INTO package_store (name, kind) VALUES ('local disk', 'FileSystem'), ('LOCAL DISK', 'FileSystem'), ('Other', 'FileSystem')");

        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, "20261127_package_store_name_case_insensitive.sql")));

        var names = (await QueryAsync("SELECT id, name FROM package_store ORDER BY id")).Select(r => (string)r["name"]).ToList();

        names.Count.ShouldBe(4);
        names[0].ShouldBe("Local disk", "the oldest keeps its name");
        names.Select(n => n.ToLowerInvariant()).Distinct().Count().ShouldBe(4);
        names[1].ShouldStartWith("local disk (");
        names[2].ShouldStartWith("LOCAL DISK (");
        names[3].ShouldBe("Other");
    }
}
