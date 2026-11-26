using Npgsql;

namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// Drops a database a test created for itself. DROP DATABASE checkpoints the server, which under the full parallel suite can take longer than Npgsql's
/// 30 second default command timeout; the drop then fails the test in <c>Dispose</c> although the test itself passed.
/// </summary>
public static class ThrowawayDatabase
{
    public const int DropTimeoutSeconds = 180;

    public static void Drop(string connectionString, string databaseName)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };

        using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        connection.Open();

        using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection) { CommandTimeout = DropTimeoutSeconds };
        command.ExecuteNonQuery();
    }
}
