using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Setup.Configuration;
using Squid.Core.Setup.Embedded;
using Squid.Core.Setup.Secrets;
using Squid.Core.Settings.Setup;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

public sealed partial class SetupInstaller
{
    public const string ConnectionStringKey = "SquidStore:ConnectionString";
    public const string RedisConnectionStringKey = "RedisCacheConnectionString";

    private const string EmbeddedPostgresPrefix = "EmbeddedServices:Postgres:";
    private const string EmbeddedRedisPrefix = "EmbeddedServices:Redis:";

    private Task<string> PrepareDatabaseAsync(CancellationToken ct) => _ctx.Request.Database.Mode == SetupServiceMode.Builtin ? StartBuiltinDatabaseAsync(ct) : CheckExternalDatabaseAsync(ct);

    private Task<string> PrepareRedisAsync(CancellationToken ct) => _ctx.Request.Redis.Mode == SetupServiceMode.Builtin ? StartBuiltinRedisAsync(ct) : CheckExternalRedisAsync();

    private async Task<string> StartBuiltinDatabaseAsync(CancellationToken ct)
    {
        var settings = _ctx.Embedded.Postgres;

        settings.Password = ResolveEmbeddedPassword(settings.Password, EmbeddedPostgresPrefix + "Password");

        // Persist the cluster password BEFORE initdb: a retry after a later failure must reuse the
        // cluster already initialised with it rather than generate a new, non-matching one.
        SquidConfigurationFile.Merge(_ctx.DataDirectory.ConfigurationFilePath, new Dictionary<string, string> { [EmbeddedPostgresPrefix + "Password"] = settings.Password });

        _ctx.DatabaseConnectionString = await _embedded.StartPostgresAsync(settings, ct).ConfigureAwait(false);
        _ctx.ConfigurationToPersist[EmbeddedPostgresPrefix + "Enabled"] = "true";

        return "Built-in PostgreSQL is running";
    }

    /// <summary>Configured value, else one an earlier (failed) attempt already persisted, else a new one.</summary>
    private string ResolveEmbeddedPassword(string configured, string key)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var persisted = SquidConfigurationFile.GetValue(_ctx.DataDirectory.ConfigurationFilePath, key);

        return string.IsNullOrWhiteSpace(persisted) ? RandomSecret.Alphanumeric(32) : persisted;
    }

    private async Task<string> CheckExternalDatabaseAsync(CancellationToken ct)
    {
        var database = _ctx.Request.Database;
        var probe = await DatabaseConnectionTester.TestAsync(database.ConnectionString, ct).ConfigureAwait(false);

        if (!probe.Success)
            throw new InvalidOperationException(probe.Message);

        if (!probe.DatabaseExists && !database.CreateIfMissing)
            throw new InvalidOperationException("The database does not exist and 'create if missing' was not selected");

        _ctx.DatabaseConnectionString = database.ConnectionString;
        _ctx.ConfigurationToPersist[EmbeddedPostgresPrefix + "Enabled"] = "false";

        return probe.Message;
    }

    private async Task<string> StartBuiltinRedisAsync(CancellationToken ct)
    {
        var settings = _ctx.Embedded.Redis;

        settings.Password = ResolveEmbeddedPassword(settings.Password, EmbeddedRedisPrefix + "Password");

        _ctx.RedisConnectionString = await _embedded.StartRedisAsync(settings, ct).ConfigureAwait(false);
        _ctx.ConfigurationToPersist[EmbeddedRedisPrefix + "Enabled"] = "true";
        _ctx.ConfigurationToPersist[EmbeddedRedisPrefix + "Password"] = settings.Password;

        return "Built-in Redis is running";
    }

    private async Task<string> CheckExternalRedisAsync()
    {
        var connectionString = _ctx.Request.Redis.ConnectionString;
        var probe = await RedisConnectionTester.TestAsync(connectionString).ConfigureAwait(false);

        if (!probe.Success)
            throw new InvalidOperationException($"Cannot connect to Redis: {probe.Message}");

        _ctx.RedisConnectionString = connectionString;
        _ctx.ConfigurationToPersist[EmbeddedRedisPrefix + "Enabled"] = "false";

        return probe.Message;
    }

    private async Task<string> MigrateDatabaseAsync(CancellationToken ct)
    {
        await Task.Run(() => new DbUpRunner(_ctx.DatabaseConnectionString).Run(), ct).ConfigureAwait(false);

        return "Database schema is up to date";
    }

    private Task<string> PersistConfigurationAsync(CancellationToken ct)
    {
        CollectConnectionStrings();
        CollectAdministrator();
        CollectServerUrls();

        SquidConfigurationFile.Merge(_ctx.DataDirectory.ConfigurationFilePath, _ctx.ConfigurationToPersist);

        return Task.FromResult($"Saved to {_ctx.DataDirectory.ConfigurationFilePath}");
    }

    private void CollectConnectionStrings()
    {
        _ctx.ConfigurationToPersist[ConnectionStringKey] = _ctx.DatabaseConnectionString;
        _ctx.ConfigurationToPersist[RedisConnectionStringKey] = _ctx.RedisConnectionString;
    }

    private void CollectAdministrator()
    {
        var admin = _ctx.Request.Admin;
        var hash = new PasswordHasher<UserAccount>().HashPassword(new UserAccount { UserName = admin.UserName }, admin.Password);

        _ctx.ConfigurationToPersist[SetupAdminSetting.SectionName + ":UserName"] = admin.UserName.Trim();
        _ctx.ConfigurationToPersist[SetupAdminSetting.SectionName + ":PasswordHash"] = hash;
    }

    private void CollectServerUrls()
    {
        var server = _ctx.Request.Server;

        if (!string.IsNullOrWhiteSpace(server.ExternalUrl)) _ctx.ConfigurationToPersist["ServerUrl:ExternalUrl"] = server.ExternalUrl.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(server.CommsUrl)) _ctx.ConfigurationToPersist["ServerUrl:CommsUrl"] = server.CommsUrl.Trim().TrimEnd('/');
    }
}
