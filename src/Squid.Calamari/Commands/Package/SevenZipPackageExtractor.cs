using SharpCompress.Archives.SevenZip;

namespace Squid.Calamari.Commands.Package;

/// <summary>
/// PR-11 — <c>.7z</c> archive extractor backed by <c>SharpCompress</c>
/// (managed, no external <c>7z</c> binary). Same hostile-archive defence
/// as <see cref="ZipPackageExtractor"/> / <see cref="TarPackageExtractor"/>:
/// zip-slip rejection, per-entry + total-archive size caps, fail-closed —
/// all via the shared <see cref="ArchiveSafety"/> primitives so every
/// format hits one identical safety story.
///
/// <para><b>Size cap is pre-decompression</b>: SharpCompress exposes each
/// entry's uncompressed <see cref="SharpCompress.Archives.SevenZip.SevenZipArchiveEntry.Size"/>
/// from the 7z header BEFORE the entry stream is opened, so a zip-bomb
/// entry is rejected by <see cref="ArchiveSafety.CheckPerEntryCap"/> without
/// ever being decompressed to disk. (Matches the declared-size model the
/// zip + tar extractors use — consistent across formats.)</para>
///
/// <para><b>Untrusted input</b>: SharpCompress surfaces corrupt / truncated
/// archives as <see cref="IOException"/> (incl. <c>EndOfStreamException</c>)
/// or <see cref="InvalidOperationException"/>, and encrypted archives as
/// <see cref="System.Security.Cryptography.CryptographicException"/>. All
/// are caught and converted to a structured failure — extraction is an
/// untrusted-input boundary, so a malformed package halts the deploy with
/// a clear reason instead of crashing the pipeline.</para>
/// </summary>
internal sealed class SevenZipPackageExtractor : IPackageExtractor
{
    public bool CanHandle(string archivePath)
        => string.Equals(Path.GetExtension(archivePath), ".7z", StringComparison.OrdinalIgnoreCase);

    public ExtractResult Extract(string archivePath, string destinationDir)
    {
        if (!File.Exists(archivePath))
            return ExtractResult.Failure($"Archive '{archivePath}' does not exist.");

        Directory.CreateDirectory(destinationDir);
        var canonicalDest = ArchiveSafety.EnsureTrailingSeparator(destinationDir);

        try
        {
            using var stream = File.OpenRead(archivePath);
            using var archive = SevenZipArchive.Open(stream);
            return ExtractEntries(archive, canonicalDest);
        }
        catch (Exception ex) when (
            ex is IOException
               or InvalidOperationException
               or UnauthorizedAccessException
               or System.Security.Cryptography.CryptographicException)
        {
            return ExtractResult.Failure(
                $"7z archive '{archivePath}' could not be extracted (corrupt, truncated, or encrypted): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ExtractResult ExtractEntries(SevenZipArchive archive, string canonicalDest)
    {
        int filesExtracted = 0;
        long totalBytesWritten = 0;

        foreach (var entry in archive.Entries)
        {
            // Malformed headers can yield a null/empty key — skip rather than NRE.
            if (string.IsNullOrEmpty(entry.Key)) continue;

            var entryPath = ArchiveSafety.ResolveSafeEntryPath(entry.Key, canonicalDest);
            if (entryPath is null)
                return ExtractResult.Failure($"Entry '{entry.Key}' would escape the destination directory (7z-slip). Aborted.");

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(entryPath);
                continue;
            }

            if (entry.Size < 0)
                return ExtractResult.Failure($"Entry '{entry.Key}' has negative size {entry.Size} — malformed 7z header.");

            var perEntryFailure = ArchiveSafety.CheckPerEntryCap(entry.Key, entry.Size);
            if (perEntryFailure is not null) return perEntryFailure;

            var totalFailure = ArchiveSafety.CheckTotalCap(entry.Key, totalBytesWritten + entry.Size);
            if (totalFailure is not null) return totalFailure;

            var parentDir = Path.GetDirectoryName(entryPath);
            if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

            // Overwrite-on-collision matches the zip + tar extractors — a
            // re-deploy with the same archive lands cleanly.
            using (var entryStream = entry.OpenEntryStream())
            using (var output = File.Create(entryPath))
                entryStream.CopyTo(output);

            filesExtracted++;
            totalBytesWritten += entry.Size;
        }

        return ExtractResult.Success(filesExtracted, totalBytesWritten);
    }
}
