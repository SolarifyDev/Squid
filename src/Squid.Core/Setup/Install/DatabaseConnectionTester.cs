using Npgsql;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

/// <summary>
/// Probes an external PostgreSQL for the setup wizard: can we log in, which server is it, does the
/// target database exist yet, and if not, may this role create it.
/// </summary>
public static class DatabaseConnectionTester
{
    private const string InvalidCatalogName = "3D000";
    private const int TimeoutSeconds = 5;

    public static async Task<DatabaseConnectionTestResult> TestAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            var builder = ParseOrThrow(connectionString);

            return await ProbeTargetDatabaseAsync(builder, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogName)
        {
            return await ProbeMaintenanceDatabaseAsync(ParseOrThrow(connectionString), ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Failure($"Invalid connection string: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Failure(ex.Message);
        }
    }

    public static NpgsqlConnectionStringBuilder ParseOrThrow(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is empty");

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = TimeoutSeconds, Pooling = false };

        if (string.IsNullOrWhiteSpace(builder.Host))
            throw new ArgumentException("Connection string has no Host");

        if (string.IsNullOrWhiteSpace(builder.Database))
            throw new ArgumentException("Connection string has no Database");

        return builder;
    }

    private static async Task<DatabaseConnectionTestResult> ProbeTargetDatabaseAsync(NpgsqlConnectionStringBuilder builder, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        return new DatabaseConnectionTestResult
        {
            Success = true,
            ServerVersion = connection.PostgreSqlVersion.ToString(),
            DatabaseExists = true,
            CanCreateDatabase = true,
            Message = $"Connected to database '{builder.Database}'"
        };
    }

    private static async Task<DatabaseConnectionTestResult> ProbeMaintenanceDatabaseAsync(NpgsqlConnectionStringBuilder target, CancellationToken ct)
    {
        try
        {
            var maintenance = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };

            await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var canCreate = await CanCreateDatabaseAsync(connection, ct).ConfigureAwait(false);
            var message = canCreate ? $"Database '{target.Database}' does not exist yet and will be created" : $"Database '{target.Database}' does not exist and this user is not allowed to create it";

            return new DatabaseConnectionTestResult
            {
                Success = canCreate,
                ServerVersion = connection.PostgreSqlVersion.ToString(),
                DatabaseExists = false,
                CanCreateDatabase = canCreate,
                Message = message
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Failure($"Database '{target.Database}' does not exist, and the server could not be checked for permission to create it: {ex.Message}");
        }
    }

    private static async Task<bool> CanCreateDatabaseAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", connection);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
    }

    private static DatabaseConnectionTestResult Failure(string message) => new() { Success = false, Message = message };
}
