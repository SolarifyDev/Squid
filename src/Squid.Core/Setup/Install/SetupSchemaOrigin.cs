using Npgsql;

namespace Squid.Core.Setup.Install;

/// <summary>
/// Remembers, across the attempts of one setup session, whether a database had no Squid schema when the setup first looked at it. Only then are the
/// package store rows the setup's own to change: against a database that already holds a Squid install they belong to its operator. The answer belongs
/// to one database (host, port and name), taken before the first migration against it and kept, so a retry after the setup itself created the schema
/// still counts as fresh, while a retry that points at a different database looks at that one.
/// </summary>
public sealed class SetupSchemaOrigin
{
    private const string InvalidCatalogName = "3D000";
    private const string SchemaExistsSql = "SELECT to_regclass('public.schemaversions') IS NOT NULL OR to_regclass('public.package_store') IS NOT NULL";

    private readonly Dictionary<string, bool> _freshByTarget = new();

    /// <summary>True only when the setup looked at this database before migrating it and found no Squid schema; false when it has not looked yet.</summary>
    public bool WasFresh(string connectionString) => _freshByTarget.TryGetValue(TargetKey(connectionString), out var fresh) && fresh;

    public async Task RecordAsync(string connectionString, CancellationToken ct)
    {
        var key = TargetKey(connectionString);

        if (_freshByTarget.ContainsKey(key)) return;

        _freshByTarget[key] = !await SchemaExistsAsync(connectionString, ct).ConfigureAwait(false);
    }

    /// <summary>Identifies the database a connection string points at, ignoring credentials and tuning so changing those does not make it another database.</summary>
    public static string TargetKey(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        return $"{builder.Host?.Trim().ToLowerInvariant()}:{builder.Port}/{builder.Database}";
    }

    private static async Task<bool> SchemaExistsAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);

            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(SchemaExistsSql, connection);

            return (bool)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogName)
        {
            return false;
        }
    }
}
