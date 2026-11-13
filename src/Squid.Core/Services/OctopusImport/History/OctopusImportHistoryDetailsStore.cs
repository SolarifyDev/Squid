using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.History;

public sealed class OctopusImportHistoryDetailsStore : IOctopusImportHistoryDetailsStore
{
    public const string DirectoryName = "history-details";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task WriteAsync(string sessionDirectory, string taskId, OctopusTaskHistoryDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);

        var path = PathFor(sessionDirectory, taskId);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);

        await JsonSerializer.SerializeAsync(gzip, details, JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<OctopusTaskHistoryDetails> ReadAsync(string sessionDirectory, string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) || string.IsNullOrWhiteSpace(taskId)) return null;

        var path = PathFor(sessionDirectory, taskId);

        if (!File.Exists(path)) return null;

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);

        return await JsonSerializer.DeserializeAsync<OctopusTaskHistoryDetails>(gzip, JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>File names are a hash of the normalised task id, never the raw id, so a hostile id cannot steer the path.</summary>
    public static string PathFor(string sessionDirectory, string taskId)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
            throw new ArgumentException("A session directory is required.", nameof(sessionDirectory));

        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("A task id is required.", nameof(taskId));

        return Path.Combine(sessionDirectory, DirectoryName, $"{FileKey(taskId)}.json.gz");
    }

    private static string FileKey(string taskId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId.Trim().ToUpperInvariant()))).ToLowerInvariant();
}
