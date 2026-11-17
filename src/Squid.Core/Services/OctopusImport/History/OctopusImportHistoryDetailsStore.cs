using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.History;

public sealed class OctopusImportHistoryDetailsStore(IOctopusImportSessionFileStore files) : IOctopusImportHistoryDetailsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<OctopusImportSessionFileWrite> WriteAsync(Guid sessionId, string taskId, OctopusTaskHistoryDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);

        var path = PathFor(taskId);

        using var content = await CompressAsync(details, ct).ConfigureAwait(false);

        return await files.WriteAsync(sessionId, path, content, OctopusImportSessionFileEncoding.Identity, ct).ConfigureAwait(false);
    }

    private static async Task<MemoryStream> CompressAsync(OctopusTaskHistoryDetails details, CancellationToken ct)
    {
        var content = new MemoryStream();

        await using (var gzip = new GZipStream(content, CompressionLevel.Optimal, leaveOpen: true))
            await JsonSerializer.SerializeAsync(gzip, details, JsonOptions, ct).ConfigureAwait(false);

        content.Position = 0;

        return content;
    }

    public async Task<OctopusTaskHistoryDetails> ReadAsync(Guid sessionId, string taskId, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(taskId)) return null;

        var stored = await files.ReadAsync(sessionId, PathFor(taskId), ct).ConfigureAwait(false);

        if (stored == null) return null;

        await using var gzip = new GZipStream(new MemoryStream(stored, writable: false), CompressionMode.Decompress);

        return await JsonSerializer.DeserializeAsync<OctopusTaskHistoryDetails>(gzip, JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(Guid sessionId, CancellationToken ct = default) => (await files.TotalsAsync(sessionId, OctopusImportSessionFilePaths.DetailsPrefix, ct).ConfigureAwait(false)).Files;

    /// <summary>Stored under a hash of the normalised task id, never the raw id, so a hostile id cannot steer the path.</summary>
    public static string PathFor(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("A task id is required.", nameof(taskId));

        return $"{OctopusImportSessionFilePaths.DetailsPrefix}{FileKey(taskId)}.json.gz";
    }

    private static string FileKey(string taskId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId.Trim().ToUpperInvariant()))).ToLowerInvariant();
}
