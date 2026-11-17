using System.Collections.Concurrent;
using Npgsql;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

/// <summary>
/// <see cref="IOctopusImportSessionFileStore"/> on the session's own tables. Every call opens its own pooled connection from the scoped
/// <see cref="SquidDbContext"/>'s connection string (never the shared configuration, which tests and other tools may point elsewhere),
/// so reads may run side by side while the fetch reads ahead, and never touch the context's own connection or transaction.
/// Files are sealed with the session's key when the master key is available; without it they are stored as they are and a warning says so.
/// </summary>
public sealed partial class OctopusImportSessionFileStore(SquidDbContext db, Lazy<IVariableEncryptionService> encryption) : IOctopusImportSessionFileStore
{
    private const int LongCommandSeconds = 600;

    private readonly ConcurrentDictionary<Guid, SessionKey> _keys = new();

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(db.Database.GetConnectionString());

        await connection.OpenAsync(ct).ConfigureAwait(false);

        return connection;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);

        foreach (var value in values) command.Parameters.Add(Parameter(value));

        return command;
    }

    private static NpgsqlBatchCommand BatchCommand(string sql, params object[] values)
    {
        var command = new NpgsqlBatchCommand(sql);

        foreach (var value in values) command.Parameters.Add(Parameter(value));

        return command;
    }

    // Chunks are passed as the reused encode buffer, never copied into an array of their own.
    private static NpgsqlParameter Parameter(object value)
        => value is ReadOnlyMemory<byte> bytes ? new NpgsqlParameter<ReadOnlyMemory<byte>> { TypedValue = bytes, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea } : new NpgsqlParameter { Value = value ?? DBNull.Value };

    private static void EnsureSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session id is required.", nameof(sessionId));
    }

    private static void EnsurePath(string path) => OctopusImportSessionFilePaths.EnsureRelative(path);

    // LIKE with the prefix taken literally; the "C" collation makes it a range scan on the primary key.
    private static string PrefixPattern(string prefix) => (prefix ?? string.Empty).Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    // ── Session key ─────────────────────────────────────────────────────────

    // Read once per scope. A wrapped key this server cannot unwrap makes every sealed file of the session unreadable, never "unsealed".
    private async Task<SessionKey> ReadKeyAsync(Guid sessionId, CancellationToken ct)
    {
        if (_keys.TryGetValue(sessionId, out var cached)) return cached;

        var wrapped = await ReadWrappedKeyAsync(sessionId, ct).ConfigureAwait(false);
        var key = wrapped == null ? SessionKey.NoKey : UnwrapKey(wrapped);

        _keys[sessionId] = key;

        return key;
    }

    // The first write of a session creates its key; a concurrent first write may win, so the stored key is read back either way.
    private async Task<byte[]> WriteKeyAsync(Guid sessionId, CancellationToken ct)
    {
        var current = await ReadKeyAsync(sessionId, ct).ConfigureAwait(false);

        if (current.Unreadable)
            throw new OctopusImportSessionFileUnreadableException();

        if (current.Key != null || current.Unprotected) return current.Key;

        var service = EncryptionOrNull(sessionId);

        if (service == null)
        {
            _keys[sessionId] = SessionKey.UnprotectedKey;
            return null;
        }

        await StoreWrappedKeyIfAbsentAsync(sessionId, OctopusImportSessionFileKeys.Wrap(service, OctopusImportSessionFileKeys.NewKey()), ct).ConfigureAwait(false);

        _keys.TryRemove(sessionId, out _);

        return (await ReadKeyAsync(sessionId, ct).ConfigureAwait(false)).Key;
    }

    private SessionKey UnwrapKey(string wrapped)
    {
        var service = EncryptionOrNull(null);
        var key = service == null ? null : OctopusImportSessionFileKeys.UnwrapOrNull(service, wrapped);

        return key == null ? SessionKey.UnreadableKey : new SessionKey(key, false, false);
    }

    // Strict mode with an empty or weak master key: the service cannot be built. Files are then stored unsealed, as the documented default.
    private IVariableEncryptionService EncryptionOrNull(Guid? sessionId)
    {
        try
        {
            return encryption.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (sessionId != null) Log.Warning("Octopus import session {SessionId} stores its files without encryption because the variable-encryption master key is unavailable ({ExceptionType})", sessionId, ex.GetType().Name);

            return null;
        }
    }

    private async Task<string> ReadWrappedKeyAsync(Guid sessionId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, "SELECT protected_file_key FROM octopus_import_session WHERE session_id = $1", sessionId);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private async Task StoreWrappedKeyIfAbsentAsync(Guid sessionId, string wrapped, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, "UPDATE octopus_import_session SET protected_file_key = $2 WHERE session_id = $1 AND protected_file_key IS NULL AND temporary_upload_cleaned_at IS NULL", sessionId, wrapped);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record SessionKey(byte[] Key, bool Unreadable, bool Unprotected)
    {
        public static readonly SessionKey NoKey = new(null, false, false);
        public static readonly SessionKey UnreadableKey = new(null, true, false);
        public static readonly SessionKey UnprotectedKey = new(null, false, true);
    }
}
