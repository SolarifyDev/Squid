using Npgsql;

namespace Squid.Core.Persistence.Db;

/// <summary>
/// Tells a database failure that may succeed when the same work runs again (serialization failure, deadlock, lost connection, server
/// restarting, command timeout) from one that will fail again. The SQLSTATE list is explicit, not Npgsql's own <c>IsTransient</c>, so a
/// driver upgrade cannot silently change what a caller retries; it is pinned by a test.
/// </summary>
public static class PostgresTransientFailureClassifier
{
    public static readonly IReadOnlySet<string> TransientSqlStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "40001", // serialization_failure
        "40P01", // deadlock_detected
        "55P03", // lock_not_available
        "57P01", // admin_shutdown
        "57P02", // crash_shutdown
        "57P03", // cannot_connect_now
        "53300", // too_many_connections
        "08000", // connection_exception
        "08001", // sqlclient_unable_to_establish_sqlconnection
        "08003", // connection_does_not_exist
        "08004", // sqlserver_rejected_establishment_of_sqlconnection
        "08006"  // connection_failure
    };

    public static bool IsTransient(Exception exception) => exception switch
    {
        null => false,
        OperationCanceledException => false,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTransient),
        PostgresException postgres => TransientSqlStates.Contains(postgres.SqlState),
        NpgsqlException npgsql => npgsql.IsTransient || IsTransient(npgsql.InnerException),
        TimeoutException => true,
        _ => IsTransient(exception.InnerException)
    };

    /// <summary>The SQLSTATE of the first Postgres error in the chain, for logs and messages; null when there is none.</summary>
    public static string SqlStateOf(Exception exception) => exception switch
    {
        null => null,
        PostgresException postgres => postgres.SqlState,
        AggregateException aggregate => aggregate.InnerExceptions.Select(SqlStateOf).FirstOrDefault(s => s != null),
        _ => SqlStateOf(exception.InnerException)
    };
}
