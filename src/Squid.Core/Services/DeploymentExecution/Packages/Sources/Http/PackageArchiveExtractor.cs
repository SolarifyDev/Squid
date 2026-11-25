using System.Formats.Tar;
using System.IO.Compression;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;

internal static class PackageArchiveExtractor
{
    private static readonly HashSet<string> YamlExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".yaml", ".yml", ".json" };

    internal static Dictionary<string, byte[]> Extract(byte[] archiveBytes)
    {
        if (IsTarGz(archiveBytes))
            return ExtractTarGz(archiveBytes);

        if (IsZip(archiveBytes))
            return ExtractZip(archiveBytes);

        // Plain POSIX tar has no magic header that is always reliable at offset 0;
        // try TarReader and fall back with a clear operator message.
        if (TryExtractTar(archiveBytes, out var tarFiles))
            return tarFiles;

        throw new InvalidOperationException("Unsupported archive format. Expected .tar.gz, .tar, or .zip.");
    }

    private static bool IsTarGz(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static bool TryExtractTar(byte[] archiveBytes, out Dictionary<string, byte[]> files)
    {
        files = new Dictionary<string, byte[]>();
        try
        {
            using var memoryStream = new MemoryStream(archiveBytes);
            using var tarReader = new TarReader(memoryStream);
            while (tarReader.GetNextEntry(copyData: true) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    continue;
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.DataStream is null)
                    continue;

                using var ms = new MemoryStream();
                entry.DataStream.CopyTo(ms);
                files[NormalizeEntryPath(entry.Name)] = ms.ToArray();
            }

            return files.Count > 0;
        }
        catch
        {
            files = new Dictionary<string, byte[]>();
            return false;
        }
    }

    private static Dictionary<string, byte[]> ExtractTarGz(byte[] archiveBytes)
    {
        var files = new Dictionary<string, byte[]>();

        using var memoryStream = new MemoryStream(archiveBytes);
        using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (tarReader.GetNextEntry(copyData: true) is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile) continue;
            if (!IsIncludedFile(entry.Name)) continue;

            var relativePath = NormalizeEntryPath(entry.Name);
            if (string.IsNullOrEmpty(relativePath)) continue;

            using var entryStream = entry.DataStream;

            if (entryStream == null) continue;

            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            files[relativePath] = ms.ToArray();
        }

        return files;
    }

    private static Dictionary<string, byte[]> ExtractZip(byte[] archiveBytes)
    {
        var files = new Dictionary<string, byte[]>();

        using var memoryStream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!IsIncludedFile(entry.FullName)) continue;

            var relativePath = NormalizeEntryPath(entry.FullName);
            if (string.IsNullOrEmpty(relativePath)) continue;

            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            files[relativePath] = ms.ToArray();
        }

        return files;
    }

    private static bool IsIncludedFile(string path)
    {
        var ext = Path.GetExtension(path);
        return YamlExtensions.Contains(ext);
    }

    private static string NormalizeEntryPath(string entryPath)
    {
        var normalized = PackageArchiveSafety.StripLeadingCurrentDirectory(entryPath).TrimStart('/');

        // Strip leading directory (common in archives: chartname/templates/file.yaml)
        var firstSlash = normalized.IndexOf('/');
        if (firstSlash > 0 && firstSlash < normalized.Length - 1)
            normalized = normalized[(firstSlash + 1)..];

        return normalized;
    }
}
