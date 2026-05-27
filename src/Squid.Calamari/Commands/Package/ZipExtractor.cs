using System.IO.Compression;

namespace Squid.Calamari.Commands.Package;

/// <summary>
/// G1.4 — zip / nupkg extractor. After PR-2 multi-format dispatch this
/// is one of several <see cref="IPackageExtractor"/> implementations,
/// selected by extension via <see cref="PackageExtractorRegistry"/>.
///
/// <para><b>Why nupkg + zip share the same code</b>: a <c>.nupkg</c> is
/// a zip file with a different filename suffix. Same binary format, same
/// engine.</para>
///
/// <para>Safety primitives (zip-slip / size caps / fail-closed) come from
/// <see cref="ArchiveSafety"/> — shared with the tar / tar.gz extractors
/// added in PR-2 so the hostile-archive defence story is identical across
/// formats.</para>
/// </summary>
internal sealed class ZipPackageExtractor : IPackageExtractor
{
    private static readonly string[] Extensions = { ".zip", ".nupkg" };

    public bool CanHandle(string archivePath)
    {
        var ext = Path.GetExtension(archivePath);
        return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public ExtractResult Extract(string archivePath, string destinationDir)
        => ZipExtractor.Extract(archivePath, destinationDir);
}

/// <summary>
/// Static façade for backward-compat with G1.4 tests that drove
/// <see cref="ZipExtractor.Extract"/> directly. New code SHOULD go through
/// <see cref="PackageExtractorRegistry"/> so format dispatch happens
/// uniformly.
/// </summary>
internal static class ZipExtractor
{
    public static ExtractResult Extract(string archivePath, string destinationDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        if (!File.Exists(archivePath))
            return ExtractResult.Failure($"Archive '{archivePath}' does not exist.");

        Directory.CreateDirectory(destinationDir);

        var canonicalDest = ArchiveSafety.EnsureTrailingSeparator(destinationDir);

        int filesExtracted = 0;
        long totalBytesWritten = 0;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            foreach (var entry in archive.Entries)
            {
                // Directory entry: 0-byte, name ends with `/`. Create dir, no contents.
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    var dirPath = ArchiveSafety.ResolveSafeEntryPath(entry.FullName, canonicalDest);
                    if (dirPath is null)
                        return ExtractResult.Failure($"Entry '{entry.FullName}' would escape the destination directory (zip-slip). Aborted.");
                    Directory.CreateDirectory(dirPath);
                    continue;
                }

                // Skip 0-length non-directory entries with empty Name (zip-spec weirdness)
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var entryPath = ArchiveSafety.ResolveSafeEntryPath(entry.FullName, canonicalDest);
                if (entryPath is null)
                    return ExtractResult.Failure($"Entry '{entry.FullName}' would escape the destination directory (zip-slip). Aborted.");

                var perEntryFailure = ArchiveSafety.CheckPerEntryCap(entry.FullName, entry.Length);
                if (perEntryFailure is not null) return perEntryFailure;

                var totalFailure = ArchiveSafety.CheckTotalCap(entry.FullName, totalBytesWritten + entry.Length);
                if (totalFailure is not null) return totalFailure;

                var parentDir = Path.GetDirectoryName(entryPath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                entry.ExtractToFile(entryPath, overwrite: true);

                filesExtracted++;
                totalBytesWritten += entry.Length;
            }
        }
        catch (InvalidDataException ex)
        {
            return ExtractResult.Failure($"Archive '{archivePath}' is malformed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ExtractResult.Failure($"Failed to extract '{archivePath}': {ex.GetType().Name}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExtractResult.Failure($"Permission denied extracting '{archivePath}': {ex.Message}");
        }

        return ExtractResult.Success(filesExtracted, totalBytesWritten);
    }
}

internal sealed record ExtractResult(bool Succeeded, int FilesExtracted, long TotalBytesWritten, string? FailureReason)
{
    public static ExtractResult Success(int filesExtracted, long totalBytesWritten)
        => new(true, filesExtracted, totalBytesWritten, null);

    public static ExtractResult Failure(string reason)
        => new(false, 0, 0, reason);
}
