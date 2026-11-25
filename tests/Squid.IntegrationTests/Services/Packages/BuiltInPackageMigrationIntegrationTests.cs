using Microsoft.Extensions.Configuration;
using Npgsql;
using Squid.Core.Persistence.Db;
using Squid.Core.Settings.System;

namespace Squid.IntegrationTests.Services.Packages;

/// <summary>Runs the real migrations against a throw-away database and checks what the built-in feed's script leaves behind: its constraints are what the application's concurrency relies on.</summary>
public sealed class BuiltInPackageMigrationIntegrationTests : IDisposable
{
    private const string ScriptName = "20261127_add_builtin_package.sql";

    private readonly string _databaseName = $"squid_it_builtin_migration_{Guid.NewGuid():N}";
    private readonly string _connectionString;

    public BuiltInPackageMigrationIntegrationTests()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json", false, true).Build();
        var builder = new NpgsqlConnectionStringBuilder(new SquidConnectionString(configuration).Value) { Database = _databaseName, Pooling = false };

        _connectionString = builder.ToString();

        MigrateWithRetry();
    }

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
        var admin = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres" };

        using var connection = new NpgsqlConnection(admin.ToString());
        connection.Open();

        using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        command.ExecuteNonQuery();
    }

    private async Task<int> FeedAsync(int spaceId, string type = "BuiltIn")
    {
        return await ScalarAsync($"INSERT INTO external_feed (feed_type, feed_uri, name, slug, space_id, last_modified_date) VALUES ('{type}', '', 'f', 's', {spaceId}, now()) RETURNING id");
    }

    private Task<int> StoreAsync() => ScalarAsync("SELECT id FROM package_store LIMIT 1");

    private async Task PackageAsync(int feedId, int storeId, string packageId, string version)
    {
        await ExecuteAsync($"INSERT INTO builtin_package (space_id, feed_id, package_id, version, file_extension, size_bytes, sha256, store_id, blob_key, source) VALUES (1, {feedId}, '{packageId}', '{version}', '.zip', 1, 'aa', {storeId}, 'k', 'api')");
    }

    [Fact]
    public async Task ASpace_CanHaveOnlyOneBuiltInFeed_ButAnyNumberOfOthers()
    {
        await FeedAsync(1);
        await FeedAsync(2);
        await FeedAsync(1, "NuGet");
        await FeedAsync(1, "NuGet");

        var second = await Should.ThrowAsync<PostgresException>(() => FeedAsync(1));

        second.ConstraintName.ShouldBe("ux_external_feed_builtin_per_space");
    }

    [Fact]
    public async Task AVersion_ExistsOncePerFeed_WhateverTheCasingOfThePackageId()
    {
        var feed = await FeedAsync(1);
        var other = await FeedAsync(2);
        var store = await StoreAsync();

        await PackageAsync(feed, store, "Acme.Web", "1.0.0");
        await PackageAsync(feed, store, "Acme.Web", "1.0.1");
        await PackageAsync(other, store, "Acme.Web", "1.0.0");

        var duplicate = await Should.ThrowAsync<PostgresException>(() => PackageAsync(feed, store, "ACME.WEB", "1.0.0"));

        duplicate.ConstraintName.ShouldBe("ux_builtin_package_feed_package_version");
    }

    [Fact]
    public async Task ThePackageNameTable_HoldsOneCasingPerPackage_AndSilentlyKeepsTheFirst()
    {
        var feed = await FeedAsync(1);

        await ExecuteAsync($"INSERT INTO builtin_package_name (feed_id, package_id) VALUES ({feed}, 'Acme.Web') ON CONFLICT (feed_id, lower(package_id)) DO NOTHING");
        await ExecuteAsync($"INSERT INTO builtin_package_name (feed_id, package_id) VALUES ({feed}, 'ACME.WEB') ON CONFLICT (feed_id, lower(package_id)) DO NOTHING");

        (await ScalarAsync($"SELECT count(*)::int FROM builtin_package_name WHERE feed_id = {feed}")).ShouldBe(1);

        var plain = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"INSERT INTO builtin_package_name (feed_id, package_id) VALUES ({feed}, 'acme.web')"));

        plain.ConstraintName.ShouldBe("ux_builtin_package_name_feed_package");
    }

    [Fact]
    public async Task AStoreWithPackages_CannotBeDeleted_AndAFeedTakesItsPackagesWithIt()
    {
        var feed = await FeedAsync(1);
        var store = await StoreAsync();

        await PackageAsync(feed, store, "Acme.Web", "1.0.0");
        await ExecuteAsync($"INSERT INTO builtin_package_name (feed_id, package_id) VALUES ({feed}, 'Acme.Web')");

        var refused = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM package_store WHERE id = {store}"));

        refused.SqlState.ShouldBe("23503");

        await ExecuteAsync($"DELETE FROM external_feed WHERE id = {feed}");

        (await ScalarAsync("SELECT count(*)::int FROM builtin_package")).ShouldBe(0);
        (await ScalarAsync("SELECT count(*)::int FROM builtin_package_name")).ShouldBe(0);
    }

    [Fact]
    public async Task RunningTheScriptAgain_ChangesNothing()
    {
        var feed = await FeedAsync(1);

        await PackageAsync(feed, await StoreAsync(), "Acme.Web", "1.0.0");

        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, ScriptName));

        await ExecuteAsync(script);
        await ExecuteAsync(script);

        (await ScalarAsync("SELECT count(*)::int FROM builtin_package")).ShouldBe(1);
        (await ScalarAsync("SELECT count(*)::int FROM external_feed")).ShouldBe(1);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
