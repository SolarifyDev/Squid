using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public sealed class OctopusImportSourceReader(IOctopusArchiveExtractor archiveExtractor, IOctopusInputExtractor inputExtractor, OctopusImportSizeLimits limits = null) : IOctopusImportSourceReader
{
    public const string HistoryBundleFileName = "octopus-history.zip";

    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    private readonly OctopusArchiveExtractionOptions _historyLimits = (limits ?? OctopusImportSizeLimits.FromEnvironment()).ExtractionOptions;

    public async Task<OctopusInputExtractionResult> ReadAsync(string temporaryUploadPath, CancellationToken ct = default)
    {
        var mainIsBundle = IsHistoryBundle(temporaryUploadPath);
        var main = await ReadFileAsync(temporaryUploadPath, mainIsBundle ? _historyLimits : null, ct).ConfigureAwait(false);
        var bundlePath = HistoryBundlePath(temporaryUploadPath);

        if (mainIsBundle || !File.Exists(bundlePath)) return main;

        var history = await ReadFileAsync(bundlePath, _historyLimits, ct).ConfigureAwait(false);

        return OctopusImportSourceMerge.Merge(main, history);
    }

    public async Task<OctopusInputExtractionResult> ReadStreamAsync(Stream content, string sourcePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        return await ReadAsync(content, sourcePath, null, ct).ConfigureAwait(false);
    }

    /// <summary>The bundle path beside a session's main file.</summary>
    public static string HistoryBundlePath(string temporaryUploadPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(temporaryUploadPath))!, HistoryBundleFileName);

    public static bool IsHistoryBundle(string path) => string.Equals(Path.GetFileName(path), HistoryBundleFileName, StringComparison.OrdinalIgnoreCase);

    private async Task<OctopusInputExtractionResult> ReadFileAsync(string path, OctopusArchiveExtractionOptions limits, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        return await ReadAsync(stream, Path.GetFileName(path), limits, ct).ConfigureAwait(false);
    }

    private async Task<OctopusInputExtractionResult> ReadAsync(Stream stream, string sourcePath, OctopusArchiveExtractionOptions limits, CancellationToken ct)
    {
        if (!await LooksLikeZipAsync(stream, ct).ConfigureAwait(false))
            return await inputExtractor.ExtractStandaloneJsonAsync(stream, sourcePath, ct: ct).ConfigureAwait(false);

        var archive = await archiveExtractor.ExtractZipAsync(stream, limits, ct).ConfigureAwait(false);

        return await inputExtractor.ExtractJsonEntriesAsync(archive.Entries, ct).ConfigureAwait(false);
    }

    private static async Task<bool> LooksLikeZipAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[ZipMagic.Length];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct).ConfigureAwait(false);

        stream.Position = 0;

        return read == ZipMagic.Length && header.AsSpan().SequenceEqual(ZipMagic);
    }
}
