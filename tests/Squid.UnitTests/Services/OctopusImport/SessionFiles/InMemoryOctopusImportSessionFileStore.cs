using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.SessionFiles;

/// <summary>
/// The session file store's contract without a database: ordinal paths, whole-file replace, writes refused once a session is purged.
/// The database store itself is covered by its integration tests; this one lets unit tests see what was stored and lose it on purpose.
/// </summary>
public sealed class InMemoryOctopusImportSessionFileStore : IOctopusImportSessionFileStore
{
    private readonly ConcurrentDictionary<(Guid Session, string Path), byte[]> _files = new();
    private readonly ConcurrentDictionary<Guid, bool> _purged = new();

    public int Reads { get; private set; }

    public IReadOnlyList<string> Paths(Guid sessionId) => _files.Keys.Where(k => k.Session == sessionId).Select(k => k.Path).Order(StringComparer.Ordinal).ToList();

    public bool Delete(Guid sessionId, string path) => _files.TryRemove((sessionId, path), out _);

    public void DeleteSession(Guid sessionId)
    {
        foreach (var key in _files.Keys.Where(k => k.Session == sessionId)) _files.TryRemove(key, out _);
    }

    public void Purge(Guid sessionId)
    {
        DeleteSession(sessionId);
        _purged[sessionId] = true;
    }

    public void Put(Guid sessionId, string path, byte[] content) => _files[(sessionId, path)] = content;

    public async Task<OctopusImportSessionFileWrite> WriteAsync(Guid sessionId, string path, Stream content, OctopusImportSessionFileEncoding encoding, CancellationToken ct = default)
    {
        if (_purged.ContainsKey(sessionId))
            throw new OctopusImportHistoryStagingLostException();

        using var buffer = new MemoryStream();

        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);

        var previous = _files.TryGetValue((sessionId, path), out var old) ? (long?)old.LongLength : null;

        _files[(sessionId, path)] = buffer.ToArray();

        return new OctopusImportSessionFileWrite(buffer.Length, previous);
    }

    public Task<OctopusImportSessionFileHeader> GetHeaderAsync(Guid sessionId, string path, CancellationToken ct = default)
        => Task.FromResult(_files.TryGetValue((sessionId, path), out var content) ? Header(path, content) : null);

    public Task<byte[]> ReadAsync(Guid sessionId, string path, CancellationToken ct = default)
    {
        Reads++;

        return Task.FromResult(_files.TryGetValue((sessionId, path), out var content) ? content.ToArray() : null);
    }

    public async Task<OctopusImportSessionFileHeader> CopyToAsync(Guid sessionId, string path, Stream destination, CancellationToken ct = default)
    {
        Reads++;

        if (!_files.TryGetValue((sessionId, path), out var content)) return null;

        await destination.WriteAsync(content, ct).ConfigureAwait(false);

        return Header(path, content);
    }

    public async IAsyncEnumerable<OctopusImportSessionFileContent> ReadAllAsync(Guid sessionId, string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var path in Paths(sessionId).Where(p => p.StartsWith(prefix, StringComparison.Ordinal)))
        {
            await Task.Yield();

            if (_files.TryGetValue((sessionId, path), out var content)) yield return new OctopusImportSessionFileContent(path, content.ToArray());
        }
    }

    public Task<IReadOnlyList<OctopusImportSessionFileHeader>> ListAsync(Guid sessionId, string prefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OctopusImportSessionFileHeader>>(Paths(sessionId).Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => Header(p, _files[(sessionId, p)])).ToList());

    public async Task<OctopusImportSessionFileTotals> TotalsAsync(Guid sessionId, string prefix, CancellationToken ct = default)
    {
        var headers = await ListAsync(sessionId, prefix, ct).ConfigureAwait(false);

        return new OctopusImportSessionFileTotals(headers.Sum(h => h.Length), headers.Count);
    }

    public Task<bool> PurgeAndMarkCleanedAsync(Guid sessionId, string temporaryUploadPath, DateTimeOffset cleanedAt, CancellationToken ct = default)
    {
        Purge(sessionId);

        return Task.FromResult(true);
    }

    public Task<IReadOnlySet<Guid>> PurgedAmongAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(sessionIds.Where(_purged.ContainsKey).ToHashSet());

    private static OctopusImportSessionFileHeader Header(string path, byte[] content)
        => new(path, content.LongLength, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), 1, OctopusImportSessionFileCodec.Identity, OctopusImportSessionFileCodec.None);
}
