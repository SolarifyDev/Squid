using System.Formats.Tar;
using System.IO.Compression;

namespace Squid.Core.Services.DeploymentExecution.Packages;

/// <summary>
/// Server-side hostile-archive checks for packages acquired before upload/install.
/// BusyBox unzip (used by Alpine SSH images) silently rewrites traversal entry
/// names during <c>unzip -l</c>, so remote shell listing alone cannot fail closed.
/// </summary>
internal static class PackageArchiveSafety
{
    public static void EnsureArchiveEntriesAreSafe(byte[] archiveBytes, string packageId, string version)
    {
        if (archiveBytes is null || archiveBytes.Length == 0)
            return;

        using var stream = new MemoryStream(archiveBytes, writable: false);

        EnsureArchiveEntriesAreSafe(stream, packageId, version);
    }

    /// <summary>The same checks over a seekable stream, so a large upload is checked from its file instead of being loaded into memory. The stream is left open and rewound.</summary>
    public static void EnsureArchiveEntriesAreSafe(Stream archive, string packageId, string version)
    {
        var header = ReadHeader(archive);

        if (header.Length == 0)
            return;

        if (IsZip(header))
            EnsureZipEntriesAreSafe(archive, packageId, version);
        else if (IsTarGz(header))
            EnsureTarGzEntriesAreSafe(archive, packageId, version);
        else
            // Plain tar has no reliable magic header; only validate when entries can be read.
            EnsurePlainTarEntriesAreSafeIfPossible(archive, packageId, version);

        archive.Position = 0;
    }

    private static byte[] ReadHeader(Stream archive)
    {
        archive.Position = 0;

        var header = new byte[4];
        var read = archive.Read(header, 0, header.Length);

        archive.Position = 0;

        return header[..read];
    }

    /// <summary>
    /// Drops the leading <c>./</c> (any number) that <c>tar czf pkg.tgz .</c> puts on every entry; a bare <c>.</c> or <c>./</c> directory entry becomes empty. Both resolve
    /// inside the extraction root. Nothing else changes: <c>..</c>, absolute paths, drive letters and <c>~/</c> are left for the safety check to refuse.
    /// </summary>
    internal static string StripLeadingCurrentDirectory(string entryName)
    {
        if (entryName is null)
            return null;

        var name = entryName.Replace('\\', '/');

        while (name.StartsWith("./", StringComparison.Ordinal))
            name = name[2..];

        return name == "." ? string.Empty : name;
    }

    internal static bool IsUnsafeArchiveEntry(string entryName)
    {
        entryName = StripLeadingCurrentDirectory(entryName);

        if (string.IsNullOrWhiteSpace(entryName))
            return false;

        var normalised = entryName.Replace('\\', '/');
        if (normalised.StartsWith('/') || normalised.StartsWith("~/"))
            return true;

        // Windows drive / UNC style absolute paths.
        if (normalised.Length >= 2 && char.IsLetter(normalised[0]) && normalised[1] == ':')
            return true;
        if (normalised.StartsWith("//", StringComparison.Ordinal))
            return true;

        foreach (var segment in normalised.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".." || segment == ".")
                return true;
        }

        return false;
    }

    private static void EnsureZipEntriesAreSafe(Stream stream, string packageId, string version)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                RejectIfUnsafe(entry.FullName, packageId, version);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                $"Package {packageId} v{version} is not a readable zip archive: {ex.Message}", ex);
        }
    }

    private static void EnsureTarGzEntriesAreSafe(Stream stream, string packageId, string version)
    {
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                RejectIfUnsafe(entry.Name, packageId, version);
                SkipData(entry);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                $"Package {packageId} v{version} is not a readable tar.gz archive: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Package {packageId} v{version} is not a readable tar.gz archive: {ex.Message}", ex);
        }
    }

    private static void EnsurePlainTarEntriesAreSafeIfPossible(Stream stream, string packageId, string version)
    {
        try
        {
            using var reader = new TarReader(stream, leaveOpen: true);
            var sawEntry = false;
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                sawEntry = true;
                RejectIfUnsafe(entry.Name, packageId, version);
                SkipData(entry);
            }

            // If TarReader accepted zero entries, treat as non-tar and leave validation to extractors.
            _ = sawEntry;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Not a readable plain tar. Defer to later extractors.
            // Do not swallow InvalidOperationException from RejectIfUnsafe (zip-slip).
        }
    }

    // Over a stream that cannot seek (the gzip stream of a tar.gz) the reader does not skip an entry's data by itself: without reading it, the next entry is read from the
    // middle of this one's padding and comes out as garbage, so a perfectly good archive with more than one entry would be rejected.
    private static void SkipData(TarEntry entry) => entry.DataStream?.CopyTo(Stream.Null);

    private static void RejectIfUnsafe(string entryName, string packageId, string version)
    {
        if (!IsUnsafeArchiveEntry(entryName))
            return;

        throw new InvalidOperationException(
            $"Package {packageId} v{version} entry '{entryName}' would escape the destination directory (zip-slip). Aborted.");
    }

    internal static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    internal static bool IsTarGz(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
}
