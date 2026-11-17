using Npgsql;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

public sealed partial class OctopusImportSessionFileStore
{
    // FOR UPDATE waits for every write in flight (they hold FOR KEY SHARE) and keeps new ones out until the session is marked cleaned.
    private const string LockClosedSessionSql = "SELECT 1 FROM octopus_import_session WHERE session_id = $1 AND temporary_upload_path = $2 AND temporary_upload_cleaned_at IS NULL AND state = ANY($3) FOR UPDATE";
    private const string DeleteFilesSql = "DELETE FROM octopus_import_session_file WHERE session_id = $1";
    private const string MarkCleanedSql = "UPDATE octopus_import_session SET protected_file_key = NULL, protected_export_password = NULL, temporary_upload_path = NULL, temporary_upload_cleaned_at = $2, temporary_upload_cleanup_error = NULL, last_modified_date = $2, data_version = $3 WHERE session_id = $1";
    private const string PurgedAmongSql = "SELECT session_id FROM octopus_import_session WHERE session_id = ANY($1) AND temporary_upload_cleaned_at IS NOT NULL";

    private static readonly string[] ClosedStates = [nameof(OctopusImportSessionState.Succeeded), nameof(OctopusImportSessionState.Failed), nameof(OctopusImportSessionState.Expired)];

    // The key goes in the same transaction as the rows: with it gone, any copy of the sealed chunks left behind (dead tuples, WAL, backups) is unreadable.
    public async Task<bool> PurgeAndMarkCleanedAsync(Guid sessionId, string temporaryUploadPath, DateTimeOffset cleanedAt, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (!await LockClosedSessionAsync(connection, transaction, sessionId, temporaryUploadPath, ct).ConfigureAwait(false)) return false;

        await ExecuteAsync(connection, transaction, DeleteFilesSql, ct, sessionId).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, MarkCleanedSql, ct, sessionId, cleanedAt, Guid.NewGuid().ToByteArray()).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        _keys.TryRemove(sessionId, out _);

        return true;
    }

    public async Task<IReadOnlySet<Guid>> PurgedAmongAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default)
    {
        if (sessionIds == null || sessionIds.Count == 0) return new HashSet<Guid>();

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, PurgedAmongSql, sessionIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var purged = new HashSet<Guid>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false)) purged.Add(reader.GetGuid(0));

        return purged;
    }

    private static async Task<bool> LockClosedSessionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, string temporaryUploadPath, CancellationToken ct)
    {
        await using var command = Command(connection, LockClosedSessionSql, sessionId, temporaryUploadPath, ClosedStates);

        command.Transaction = transaction;

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) != null;
    }
}
