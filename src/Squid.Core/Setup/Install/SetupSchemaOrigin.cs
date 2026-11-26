using Npgsql;

namespace Squid.Core.Setup.Install;

/// <summary>
/// Remembers, across the attempts of one setup session, whether the database had no Squid schema when the setup first looked at it. Only then are the
/// package store rows the setup's own to change: against a database that already holds a Squid install they belong to its operator. The answer is
/// taken once, before the first migration, and kept, so a retry after the setup itself created the schema still counts as a fresh install.
/// </summary>
public sealed class SetupSchemaOrigin
{
    private const string InvalidCatalogName = "3D000";
    private const string SchemaExistsSql = "SELECT to_regclass('public.schemaversions') IS NOT NULL OR to_regclass('public.package_store') IS NOT NULL";

    /// <summary>Null until the first migration step has looked.</summary>
    public bool? DatabaseWasFresh { get; private set; }

    public async Task RecordAsync(string connectionString, CancellationToken ct)
    {
        if (DatabaseWasFresh != null) return;

        DatabaseWasFresh = !await SchemaExistsAsync(connectionString, ct).ConfigureAwait(false);
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
