using Npgsql;

namespace Squid.Core.Services.Deployments.Runbooks;

public static class RunbookDbExceptionMapper
{
    public const string NameUniqueIndex = "ux_runbook_project_normalized_name";
    public const string SnapshotVersionUniqueIndex = "uq_runbook_snapshot_version";

    public static bool IsNameConflict(Exception exception)
    {
        return FindPostgresException(exception) is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: NameUniqueIndex
        };
    }

    public static bool IsPublicationRace(Exception exception) =>
        FindPostgresException(exception) is { SqlState: PostgresErrorCodes.SerializationFailure }
            or { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: SnapshotVersionUniqueIndex }
            or { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "fk_runbook_snapshot_runbook" };

    public static bool IsPublishedDeleteConflict(Exception exception) =>
        FindPostgresException(exception) is
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: "fk_runbook_snapshot_runbook"
        };

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is PostgresException postgresException) return postgresException;
        }

        return null;
    }
}
