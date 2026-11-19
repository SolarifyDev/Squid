using Npgsql;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

public sealed partial class OctopusImportSessionFileStore
{
    // Holds the session row against the purge (FOR UPDATE) for the whole write, and refuses once the upload was cleaned up.
    private const string LockSessionSql = "SELECT 1 FROM octopus_import_session WHERE session_id = $1 AND temporary_upload_cleaned_at IS NULL FOR KEY SHARE";
    private const string DeleteFileSql = "DELETE FROM octopus_import_session_file WHERE session_id = $1 AND path = $2 RETURNING length";
    private const string InsertHeaderSql = "INSERT INTO octopus_import_session_file (session_id, path, length, sha256, chunk_count, encoding, encryption) VALUES ($1, $2, $3, $4, $5, $6, $7)";
    private const string InsertChunkSql = "INSERT INTO octopus_import_session_file_chunk (session_id, path, chunk_index, content) VALUES ($1, $2, $3, $4)";
    private const string FinishHeaderSql = "UPDATE octopus_import_session_file SET length = $3, sha256 = $4, chunk_count = $5 WHERE session_id = $1 AND path = $2";

    private static readonly string PendingSha256 = new('0', 64);

    public async Task<OctopusImportSessionFileWrite> WriteAsync(Guid sessionId, string path, Stream content, OctopusImportSessionFileEncoding encoding, CancellationToken ct = default)
    {
        EnsureSession(sessionId);
        EnsurePath(path);
        ArgumentNullException.ThrowIfNull(content);

        var key = await WriteKeyAsync(sessionId, ct).ConfigureAwait(false);

        using var encoder = new OctopusImportSessionFileChunkEncoder(sessionId, path, key, encoding);
        using var chunker = new OctopusImportSessionFileChunker(content, encoder);
        var write = new FileWrite(sessionId, path, OctopusImportSessionFileCodec.EncodingName(encoding), OctopusImportSessionFileCodec.EncryptionName(key), chunker);

        try
        {
            return await WriteInTransactionAsync(write, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // No session row to hold the file: the session is gone, as lost as a purged one.
            throw new OctopusImportHistoryStagingLostException();
        }
    }

    // A file of one chunk (every staged document) is written in one round trip plus the commit; a larger one chunk by chunk.
    private async Task<OctopusImportSessionFileWrite> WriteInTransactionAsync(FileWrite write, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var previousLength = await StartWriteAsync(connection, transaction, write, ct).ConfigureAwait(false);

        if (!write.Chunker.Completed) await WriteRemainingChunksAsync(connection, transaction, write, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new OctopusImportSessionFileWrite(write.Chunker.Length, previousLength);
    }

    private static async Task<long?> StartWriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, FileWrite write, CancellationToken ct)
    {
        var first = await write.Chunker.NextAsync(ct).ConfigureAwait(false);

        await using var batch = new NpgsqlBatch(connection, transaction);

        batch.BatchCommands.Add(BatchCommand(LockSessionSql, write.SessionId));
        batch.BatchCommands.Add(BatchCommand(DeleteFileSql, write.SessionId, write.Path));
        batch.BatchCommands.Add(HeaderCommand(write));

        if (first is { } chunk) batch.BatchCommands.Add(BatchCommand(InsertChunkSql, write.SessionId, write.Path, 0, chunk));

        return await ReadStartResultAsync(batch, ct).ConfigureAwait(false);
    }

    // Final values when the first chunk was the last; otherwise placeholders that the last statement of the transaction replaces.
    private static NpgsqlBatchCommand HeaderCommand(FileWrite write)
    {
        var chunker = write.Chunker;

        return chunker.Completed
            ? BatchCommand(InsertHeaderSql, write.SessionId, write.Path, chunker.Length, chunker.Sha256, chunker.Count, write.Encoding, write.Encryption)
            : BatchCommand(InsertHeaderSql, write.SessionId, write.Path, 0L, PendingSha256, 0, write.Encoding, write.Encryption);
    }

    private static async Task<long?> ReadStartResultAsync(NpgsqlBatch batch, CancellationToken ct)
    {
        await using var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var sessionOpen = await reader.ReadAsync(ct).ConfigureAwait(false);

        await reader.NextResultAsync(ct).ConfigureAwait(false);

        long? previousLength = await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetInt64(0) : null;

        while (await reader.NextResultAsync(ct).ConfigureAwait(false)) { }

        if (!sessionOpen)
            throw new OctopusImportHistoryStagingLostException();

        return previousLength;
    }

    private static async Task WriteRemainingChunksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, FileWrite write, CancellationToken ct)
    {
        while (await write.Chunker.NextAsync(ct).ConfigureAwait(false) is { } chunk)
            await ExecuteAsync(connection, transaction, InsertChunkSql, ct, write.SessionId, write.Path, write.Chunker.Count - 1, chunk).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, FinishHeaderSql, ct, write.SessionId, write.Path, write.Chunker.Length, write.Chunker.Sha256, write.Chunker.Count).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = Command(connection, sql, values);

        command.Transaction = transaction;
        command.CommandTimeout = LongCommandSeconds;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record FileWrite(Guid SessionId, string Path, string Encoding, string Encryption, OctopusImportSessionFileChunker Chunker);
}
