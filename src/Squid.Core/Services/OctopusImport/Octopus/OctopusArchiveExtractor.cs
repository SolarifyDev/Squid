using System.IO.Compression;

namespace Squid.Core.Services.OctopusImport.Octopus;

public interface IOctopusArchiveExtractor : IScopedDependency
{
    Task<OctopusArchiveExtractionResult> ExtractZipAsync(
        Stream archiveStream,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default);
}

public class OctopusArchiveExtractor : IOctopusArchiveExtractor
{
    private const int CopyBufferSize = 81920;
    private static readonly string PathSafetyRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "squid-octopus-import-archive-root"));
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] GzipMagic = [0x1F, 0x8B];
    private static readonly HashSet<string> NestedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".tar",
        ".gz",
        ".tgz",
        ".rar",
        ".7z"
    };

    public async Task<OctopusArchiveExtractionResult> ExtractZipAsync(
        Stream archiveStream,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);

        options ??= OctopusArchiveExtractionOptions.Default;
        options.EnsureValid();

        try
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

            if (archive.Entries.Count > options.MaxEntryCount)
                throw BuildLimitException(
                    OctopusArchiveExtractionErrorCodes.EntryCountLimitExceeded,
                    $"Octopus import archive contains {archive.Entries.Count} entries, exceeding the configured limit of {options.MaxEntryCount}.");

            var entries = new List<OctopusExtractedArchiveEntry>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalUncompressedSizeBytes = 0;

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var isDirectory = IsDirectoryEntry(entry);
                var relativePath = NormalizeAndValidateEntryPath(entry.FullName, isDirectory);

                if (IsDirectoryEntry(entry))
                    continue;

                if (!seenPaths.Add(relativePath))
                    throw BuildEntryException(
                        OctopusArchiveExtractionErrorCodes.DuplicateEntryPath,
                        $"Octopus import archive contains duplicate entry path '{relativePath}'.",
                        relativePath);

                RejectNestedArchiveByPath(relativePath);
                EnsureEntryLengthWithinLimit(entry, relativePath, options.MaxEntrySizeBytes);

                var content = await ReadEntryContentAsync(entry, relativePath, options.MaxEntrySizeBytes, ct).ConfigureAwait(false);

                RejectNestedArchiveByContent(relativePath, content);

                totalUncompressedSizeBytes = checked(totalUncompressedSizeBytes + content.LongLength);

                if (totalUncompressedSizeBytes > options.MaxTotalUncompressedSizeBytes)
                    throw BuildLimitException(
                        OctopusArchiveExtractionErrorCodes.TotalSizeLimitExceeded,
                        $"Octopus import archive uncompressed size exceeds the configured limit of {options.MaxTotalUncompressedSizeBytes} bytes.",
                        relativePath);

                entries.Add(new OctopusExtractedArchiveEntry(relativePath, content));
            }

            return new OctopusArchiveExtractionResult(entries);
        }
        catch (OctopusArchiveExtractionException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new OctopusArchiveExtractionException(
                OctopusArchiveExtractionErrorCodes.InvalidArchive,
                "Octopus import archive could not be opened as a ZIP archive.",
                innerException: ex);
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
        => string.IsNullOrEmpty(entry.Name);

    private static string NormalizeAndValidateEntryPath(string entryPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            throw BuildEntryException(
                OctopusArchiveExtractionErrorCodes.EntryPathUnsafe,
                "Octopus import archive contains an entry with an empty path.",
                entryPath);

        if (entryPath.StartsWith('/') || entryPath.StartsWith('\\') || HasDriveLetter(entryPath))
            throw BuildEntryException(
                OctopusArchiveExtractionErrorCodes.EntryPathUnsafe,
                $"Octopus import archive entry path '{entryPath}' is not relative.",
                entryPath);

        var normalized = entryPath.Replace('\\', '/');

        if (isDirectory)
            normalized = normalized.TrimEnd('/');

        var segments = normalized.Split('/');

        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
            throw BuildEntryException(
                OctopusArchiveExtractionErrorCodes.EntryPathUnsafe,
                $"Octopus import archive entry path '{entryPath}' contains an unsafe segment.",
                entryPath);

        var resolvedPath = Path.GetFullPath(Path.Combine(PathSafetyRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var expectedRoot = PathSafetyRoot.EndsWith(Path.DirectorySeparatorChar)
            ? PathSafetyRoot
            : PathSafetyRoot + Path.DirectorySeparatorChar;

        if (!resolvedPath.StartsWith(expectedRoot, StringComparison.Ordinal))
            throw BuildEntryException(
                OctopusArchiveExtractionErrorCodes.EntryPathUnsafe,
                $"Octopus import archive entry path '{entryPath}' resolves outside the extraction root.",
                entryPath);

        return normalized;
    }

    private static bool HasDriveLetter(string entryPath)
        => entryPath.Length >= 2 && entryPath[1] == ':';

    private static void RejectNestedArchiveByPath(string relativePath)
    {
        if (NestedArchiveExtensions.Contains(Path.GetExtension(relativePath)) ||
            relativePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw BuildEntryException(
                OctopusArchiveExtractionErrorCodes.NestedArchiveRejected,
                $"Octopus import archive entry '{relativePath}' appears to be a nested archive.",
                relativePath);
    }

    private static void EnsureEntryLengthWithinLimit(ZipArchiveEntry entry, string relativePath, long maxEntrySizeBytes)
    {
        if (entry.Length > maxEntrySizeBytes)
            throw BuildEntryException(
                OctopusArchiveExtractionErrorCodes.EntrySizeLimitExceeded,
                $"Octopus import archive entry '{relativePath}' exceeds the configured per-entry size limit of {maxEntrySizeBytes} bytes.",
                relativePath);
    }

    private static async Task<byte[]> ReadEntryContentAsync(
        ZipArchiveEntry entry,
        string relativePath,
        long maxEntrySizeBytes,
        CancellationToken ct)
    {
        await using var entryStream = entry.Open();
        await using var memoryStream = new MemoryStream();
        var buffer = new byte[CopyBufferSize];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var read = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);

            if (read == 0)
                break;

            if (memoryStream.Length + read > maxEntrySizeBytes)
                throw BuildEntryException(
                    OctopusArchiveExtractionErrorCodes.EntrySizeLimitExceeded,
                    $"Octopus import archive entry '{relativePath}' exceeds the configured per-entry size limit of {maxEntrySizeBytes} bytes.",
                    relativePath);

            await memoryStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return memoryStream.ToArray();
    }

    private static void RejectNestedArchiveByContent(string relativePath, byte[] content)
    {
        if (StartsWith(content, ZipMagic) || StartsWith(content, GzipMagic))
            throw BuildEntryException(
                OctopusArchiveExtractionErrorCodes.NestedArchiveRejected,
                $"Octopus import archive entry '{relativePath}' contains nested archive content.",
                relativePath);
    }

    private static bool StartsWith(byte[] content, byte[] prefix)
    {
        if (content.Length < prefix.Length)
            return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (content[i] != prefix[i])
                return false;
        }

        return true;
    }

    private static OctopusArchiveExtractionException BuildLimitException(string code, string message, string sourcePath = null)
        => new(code, message, sourcePath);

    private static OctopusArchiveExtractionException BuildEntryException(string code, string message, string sourcePath)
        => new(code, message, sourcePath);
}
