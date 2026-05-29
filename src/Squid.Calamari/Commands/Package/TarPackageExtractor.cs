using System.Formats.Tar;
using System.IO.Compression;

namespace Squid.Calamari.Commands.Package;

/// <summary>
/// PR-2 — POSIX tar archive extractor. Handles plain <c>.tar</c> via
/// <c>System.Formats.Tar.TarReader</c> (.NET 7+, no external dep). Same
/// hostile-archive defence as <see cref="ZipPackageExtractor"/>: zip-slip
/// rejection, per-entry + total-archive size caps, fail-closed.
///
/// <para><b>Symlinks &amp; device files</b>: tar can encode them via the
/// entry's <see cref="TarEntryType"/>. We extract only regular files +
/// directories; <c>SymbolicLink</c>, <c>HardLink</c>, <c>BlockDevice</c>,
/// <c>CharacterDevice</c>, <c>Fifo</c> are SKIPPED with a structured
/// console warning. Letting symlinks through would let a malicious tar
/// plant a symlink pointing outside the working dir (the
/// <see cref="Substitution.GlobMatcher"/> symlink sandbox catches OS-level
/// follow attempts, but defence-in-depth says reject at extract time too).</para>
///
/// <para><b>Long path / PAX extension</b>: <c>TarReader</c> handles
/// PAX/POSIX-1.2001 extended headers natively. Long paths that wouldn't
/// fit the v7 100-byte name field still flow through correctly.</para>
/// </summary>
internal sealed class TarPackageExtractor : IPackageExtractor
{
    public bool CanHandle(string archivePath)
        => string.Equals(Path.GetExtension(archivePath), ".tar", StringComparison.OrdinalIgnoreCase);

    public ExtractResult Extract(string archivePath, string destinationDir)
    {
        if (!File.Exists(archivePath))
            return ExtractResult.Failure($"Archive '{archivePath}' does not exist.");

        Directory.CreateDirectory(destinationDir);
        var canonicalDest = ArchiveSafety.EnsureTrailingSeparator(destinationDir);

        try
        {
            using var stream = File.OpenRead(archivePath);
            return ExtractTarStream(stream, canonicalDest, archivePath);
        }
        catch (IOException ex)
        {
            return ExtractResult.Failure($"Failed to read tar '{archivePath}': {ex.GetType().Name}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExtractResult.Failure($"Permission denied extracting '{archivePath}': {ex.Message}");
        }
    }

    /// <summary>Stream-driven extraction loop — shared between plain .tar
    /// and the tar.gz wrapper (<see cref="TarGzPackageExtractor"/>) which
    /// reuses this code by passing a <c>GZipStream</c> over the archive.</summary>
    internal static ExtractResult ExtractTarStream(Stream tarStream, string canonicalDest, string archiveLabelForLogs)
    {
        int filesExtracted = 0;
        long totalBytesWritten = 0;

        try
        {
            using var reader = new TarReader(tarStream, leaveOpen: true);

            while (reader.GetNextEntry() is { } entry)
            {
                // Skip symlinks / hardlinks / devices — see class docs.
                if (entry.EntryType is TarEntryType.SymbolicLink
                                    or TarEntryType.HardLink
                                    or TarEntryType.BlockDevice
                                    or TarEntryType.CharacterDevice
                                    or TarEntryType.Fifo)
                {
                    Console.WriteLine(
                        $"TarExtractor: skipping non-regular entry '{entry.Name}' (type {entry.EntryType}). " +
                        "Symlinks/devices are not extracted — operator-shipped archive can't grant filesystem escape this way.");
                    continue;
                }

                var entryPath = ArchiveSafety.ResolveSafeEntryPath(entry.Name, canonicalDest);
                if (entryPath is null)
                    return ExtractResult.Failure($"Entry '{entry.Name}' would escape the destination directory (tar-slip). Aborted.");

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(entryPath);
                    continue;
                }

                // Regular file (TarEntryType.RegularFile or older v7
                // TarEntryType.V7RegularFile). DataStream is null for entries
                // that don't carry payload (a zero-byte file's DataStream is
                // non-null and empty; truly null means non-file entry which
                // we filtered above).
                if (entry.DataStream is null)
                    continue;

                if (entry.Length < 0)
                    return ExtractResult.Failure($"Entry '{entry.Name}' has negative Length {entry.Length} — malformed tar header.");

                var perEntryFailure = ArchiveSafety.CheckPerEntryCap(entry.Name, entry.Length);
                if (perEntryFailure is not null) return perEntryFailure;

                var totalFailure = ArchiveSafety.CheckTotalCap(entry.Name, totalBytesWritten + entry.Length);
                if (totalFailure is not null) return totalFailure;

                var parentDir = Path.GetDirectoryName(entryPath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                // Overwrite-on-collision matches zip extractor — re-deploys
                // with the same archive land cleanly.
                using (var output = File.Create(entryPath))
                    entry.DataStream.CopyTo(output);

                filesExtracted++;
                totalBytesWritten += entry.Length;
            }
        }
        catch (InvalidDataException ex)
        {
            return ExtractResult.Failure($"Archive '{archiveLabelForLogs}' is malformed: {ex.Message}");
        }
        catch (EndOfStreamException ex)
        {
            return ExtractResult.Failure($"Archive '{archiveLabelForLogs}' is truncated: {ex.Message}");
        }

        return ExtractResult.Success(filesExtracted, totalBytesWritten);
    }
}

/// <summary>
/// PR-2 — gzipped tar (.tar.gz, .tgz). Layers a <see cref="GZipStream"/>
/// over the file and delegates to the same tar reader as
/// <see cref="TarPackageExtractor"/>. All safety primitives are identical
/// because they're applied at the entry level after the gzip layer is
/// transparent.
/// </summary>
internal sealed class TarGzPackageExtractor : IPackageExtractor
{
    private static readonly string[] Suffixes = { ".tar.gz", ".tgz" };

    public bool CanHandle(string archivePath)
    {
        // Compound suffix — .tar.gz needs special-case before .gz alone.
        // We compare against the FULL filename so ".tar.gz" matches even
        // though Path.GetExtension only returns ".gz".
        var name = Path.GetFileName(archivePath);
        return Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public ExtractResult Extract(string archivePath, string destinationDir)
    {
        if (!File.Exists(archivePath))
            return ExtractResult.Failure($"Archive '{archivePath}' does not exist.");

        Directory.CreateDirectory(destinationDir);
        var canonicalDest = ArchiveSafety.EnsureTrailingSeparator(destinationDir);

        try
        {
            using var raw = File.OpenRead(archivePath);
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            return TarPackageExtractor.ExtractTarStream(gz, canonicalDest, archivePath);
        }
        catch (InvalidDataException ex)
        {
            return ExtractResult.Failure($"Archive '{archivePath}' is not a valid gzip stream: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ExtractResult.Failure($"Failed to read tar.gz '{archivePath}': {ex.GetType().Name}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExtractResult.Failure($"Permission denied extracting '{archivePath}': {ex.Message}");
        }
    }
}
