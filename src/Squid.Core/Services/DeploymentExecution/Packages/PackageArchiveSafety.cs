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

        if (IsZip(archiveBytes))
        {
            EnsureZipEntriesAreSafe(archiveBytes, packageId, version);
            return;
        }

        if (IsTarGz(archiveBytes))
        {
            EnsureTarGzEntriesAreSafe(archiveBytes, packageId, version);
            return;
        }

        // Plain tar has no reliable magic header; only validate when entries can be read.
        EnsurePlainTarEntriesAreSafeIfPossible(archiveBytes, packageId, version);
    }

    internal static bool IsUnsafeArchiveEntry(string entryName)
    {
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

    private static void EnsureZipEntriesAreSafe(byte[] archiveBytes, string packageId, string version)
    {
        try
        {
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
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

    private static void EnsureTarGzEntriesAreSafe(byte[] archiveBytes, string packageId, string version)
    {
        try
        {
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry(copyData: false) is { } entry)
                RejectIfUnsafe(entry.Name, packageId, version);
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

    private static void EnsurePlainTarEntriesAreSafeIfPossible(byte[] archiveBytes, string packageId, string version)
    {
        try
        {
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var reader = new TarReader(stream);
            var sawEntry = false;
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                sawEntry = true;
                RejectIfUnsafe(entry.Name, packageId, version);
            }

            // If TarReader accepted zero entries, treat as non-tar and leave validation to extractors.
            _ = sawEntry;
        }
        catch
        {
            // Not a plain tar (or unreadable as tar). Defer to later extractors.
        }
    }

    private static void RejectIfUnsafe(string entryName, string packageId, string version)
    {
        if (!IsUnsafeArchiveEntry(entryName))
            return;

        throw new InvalidOperationException(
            $"Package {packageId} v{version} entry '{entryName}' would escape the destination directory (zip-slip). Aborted.");
    }

    private static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static bool IsTarGz(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
}
