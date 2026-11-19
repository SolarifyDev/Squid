using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Npgsql;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

public sealed partial class OctopusImportSessionFileStore
{
    private const string HeaderColumns = "f.path, f.length, f.sha256, f.chunk_count, f.encoding, f.encryption";

    // One statement, so one snapshot: a file being replaced is read whole, old or new, never chunks of both.
    private const string FileChunksSql = $"SELECT {HeaderColumns}, c.chunk_index, c.content FROM octopus_import_session_file f LEFT JOIN octopus_import_session_file_chunk c ON c.session_id = f.session_id AND c.path = f.path WHERE f.session_id = $1 AND f.path = $2 ORDER BY c.chunk_index";
    private const string PrefixChunksSql = $"SELECT {HeaderColumns}, c.chunk_index, c.content FROM octopus_import_session_file f LEFT JOIN octopus_import_session_file_chunk c ON c.session_id = f.session_id AND c.path = f.path WHERE f.session_id = $1 AND f.path LIKE $2 ORDER BY f.path, c.chunk_index";
    private const string HeaderSql = $"SELECT {HeaderColumns} FROM octopus_import_session_file f WHERE f.session_id = $1 AND f.path = $2";
    private const string PrefixHeadersSql = $"SELECT {HeaderColumns} FROM octopus_import_session_file f WHERE f.session_id = $1 AND f.path LIKE $2 ORDER BY f.path";
    private const string PrefixTotalsSql = "SELECT count(*)::int, coalesce(sum(length), 0)::bigint FROM octopus_import_session_file WHERE session_id = $1 AND path LIKE $2";
    private const string PrefixFolderCountsSql = "SELECT split_part(substr(path, $3), '/', 1), count(*)::int FROM octopus_import_session_file WHERE session_id = $1 AND path LIKE $2 GROUP BY 1";

    public async Task<OctopusImportSessionFileHeader> GetHeaderAsync(Guid sessionId, string path, CancellationToken ct = default)
    {
        EnsurePath(path);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, HeaderSql, sessionId, path);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadHeader(reader) : null;
    }

    public async Task<byte[]> ReadAsync(Guid sessionId, string path, CancellationToken ct = default)
    {
        using var content = new MemoryStream();

        return await CopyToAsync(sessionId, path, content, ct).ConfigureAwait(false) == null ? null : content.ToArray();
    }

    public async Task<OctopusImportSessionFileHeader> CopyToAsync(Guid sessionId, string path, Stream destination, CancellationToken ct = default)
    {
        EnsurePath(path);
        ArgumentNullException.ThrowIfNull(destination);

        var key = await ReadKeyAsync(sessionId, ct).ConfigureAwait(false);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, FileChunksSql, sessionId, path);

        command.CommandTimeout = LongCommandSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        return await CopyFileAsync(reader, new FileRead(sessionId, key, destination), ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<OctopusImportSessionFileContent> ReadAllAsync(Guid sessionId, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = await ReadKeyAsync(sessionId, ct).ConfigureAwait(false);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, PrefixChunksSql, sessionId, PrefixPattern(prefix));

        command.CommandTimeout = LongCommandSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var more = await reader.ReadAsync(ct).ConfigureAwait(false);

        while (more)
        {
            using var content = new MemoryStream();
            var file = new FileRead(sessionId, key, content);
            var header = await CopyFileAsync(reader, file, ct).ConfigureAwait(false);

            more = file.HasNextRow;

            yield return new OctopusImportSessionFileContent(header.Path, content.ToArray());
        }
    }

    public async Task<IReadOnlyList<OctopusImportSessionFileHeader>> ListAsync(Guid sessionId, string prefix, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, PrefixHeadersSql, sessionId, PrefixPattern(prefix));

        command.CommandTimeout = LongCommandSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var headers = new List<OctopusImportSessionFileHeader>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false)) headers.Add(ReadHeader(reader));

        return headers;
    }

    public async Task<OctopusImportSessionFileTotals> TotalsAsync(Guid sessionId, string prefix, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, PrefixTotalsSql, sessionId, PrefixPattern(prefix));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        await reader.ReadAsync(ct).ConfigureAwait(false);

        return new OctopusImportSessionFileTotals(reader.GetInt64(1), reader.GetInt32(0));
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByFolderAsync(Guid sessionId, string prefix, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, PrefixFolderCountsSql, sessionId, PrefixPattern(prefix), (prefix ?? string.Empty).Length + 1);

        command.CommandTimeout = LongCommandSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        while (await reader.ReadAsync(ct).ConfigureAwait(false)) counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    private static OctopusImportSessionFileHeader ReadHeader(NpgsqlDataReader reader)
        => new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2).Trim(), reader.GetInt32(3), reader.GetString(4), reader.GetString(5));

    // ── One file's rows ─────────────────────────────────────────────────────

    // Reads the rows of the file the reader stands on (one per chunk, columns in order), decoding each chunk into the destination and
    // leaving the reader on the next file's first row. Every chunk must be there, in order, and the bytes must hash to the header's hash.
    private static async Task<OctopusImportSessionFileHeader> CopyFileAsync(NpgsqlDataReader reader, FileRead file, CancellationToken ct)
    {
        var header = ReadHeader(reader);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = 0L;
        var chunks = 0;

        do
        {
            var chunk = await ReadChunkAsync(reader, file, header, chunks, ct).ConfigureAwait(false);

            if (chunk == null) break;

            hash.AppendData(chunk);
            length += chunk.Length;
            chunks++;

            await file.Destination.WriteAsync(chunk, ct).ConfigureAwait(false);

            file.HasNextRow = await reader.ReadAsync(ct).ConfigureAwait(false);
        }
        while (file.HasNextRow && reader.GetString(0) == header.Path);

        EnsureIntact(header, chunks, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());

        if (chunks == 0) file.HasNextRow = await reader.ReadAsync(ct).ConfigureAwait(false);

        return header;
    }

    // The header columns were read already; the chunk columns follow. Null for a file of no chunks (the LEFT JOIN's empty row).
    private static async Task<byte[]> ReadChunkAsync(NpgsqlDataReader reader, FileRead file, OctopusImportSessionFileHeader header, int expectedIndex, CancellationToken ct)
    {
        if (await reader.IsDBNullAsync(6, ct).ConfigureAwait(false)) return null;

        var index = reader.GetInt32(6);
        var stored = await reader.GetFieldValueAsync<byte[]>(7, ct).ConfigureAwait(false);

        if (index != expectedIndex)
            throw new OctopusImportSessionFileUnreadableException();

        return DecodeOrThrow(stored, file, header, index);
    }

    private static byte[] DecodeOrThrow(byte[] stored, FileRead file, OctopusImportSessionFileHeader header, int index)
    {
        if (file.Key.Unreadable && header.Encryption != OctopusImportSessionFileCodec.None)
            throw new OctopusImportSessionFileUnreadableException();

        try
        {
            return OctopusImportSessionFileCodec.Decode(stored, file.Key.Key, header.Encryption, header.Encoding, file.SessionId, header.Path, index);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            throw new OctopusImportSessionFileUnreadableException();
        }
    }

    private static void EnsureIntact(OctopusImportSessionFileHeader header, int chunks, long length, string sha256)
    {
        if (chunks != header.ChunkCount || length != header.Length || !string.Equals(sha256, header.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new OctopusImportSessionFileUnreadableException();
    }

    private sealed class FileRead(Guid sessionId, SessionKey key, Stream destination)
    {
        public Guid SessionId { get; } = sessionId;
        public SessionKey Key { get; } = key;
        public Stream Destination { get; } = destination;
        public bool HasNextRow { get; set; }
    }
}
