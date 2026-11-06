using Npgsql;

namespace Squid.Core.Services.Deployments.Runbooks;

public static class RunbookDbExceptionMapper
{
    public const string NameUniqueIndex = "ux_runbook_project_normalized_name";

    public static bool IsNameConflict(Exception exception)
    {
        return FindPostgresException(exception) is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: NameUniqueIndex
        };
    }

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is PostgresException postgresException) return postgresException;
        }

        return null;
    }
}
