using System.Formats.Tar;
using System.IO.Compression;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;

/// <summary>
/// Reads the files a consumer of <c>PackageFetchResult.Files</c> wants (the extensions <see cref="PackageArchiveExtractor"/> keeps, under the same names) out of a built-in
/// package without buffering the rest of it: tar entries are read as a stream and only the kept ones are copied, each bounded. A file or total over a bound is left out
/// with a warning instead of failing the deployment.
/// </summary>
internal static class BuiltInArchiveFileExtractor
{
    public const long MaxFileBytes = 16L * 1024 * 1024;

    public const long MaxTotalBytes = 128L * 1024 * 1024;

    public static Dictionary<string, byte[]> Extract(byte[] archiveBytes, List<string> warnings)
    {
        var files = new Dictionary<string, byte[]>();
        using var stream = new MemoryStream(archiveBytes, writable: false);

        if (archiveBytes.Length >= 2 && archiveBytes[0] == 0x1F && archiveBytes[1] == 0x8B) ReadTarGz(stream, files, warnings);
        else ReadZip(stream, files, warnings);

        return files;
    }

    private static void ReadTarGz(Stream stream, Dictionary<string, byte[]> files, List<string> warnings)
    {
        using var reader = new TarReader(new GZipStream(stream, CompressionMode.Decompress), leaveOpen: false);

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            var wanted = entry.EntryType == TarEntryType.RegularFile && entry.DataStream != null && PackageArchiveExtractor.IsIncludedFile(entry.Name);
            var name = wanted ? PackageArchiveExtractor.NormalizeEntryPath(entry.Name) : null;

            // The gzip stream cannot seek, so an entry that is not kept still has to be read past.
            if (wanted && Admit(files, warnings, name, entry.Length)) Store(files, name, entry.Length, entry.DataStream);
            else entry.DataStream?.CopyTo(Stream.Null);
        }
    }

    private static void ReadZip(Stream stream, Dictionary<string, byte[]> files, List<string> warnings)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0 && PackageArchiveExtractor.IsIncludedFile(e.FullName)))
        {
            var name = PackageArchiveExtractor.NormalizeEntryPath(entry.FullName);

            // An entry that is not kept is never opened, so a huge one costs nothing.
            if (!Admit(files, warnings, name, entry.Length)) continue;

            using var data = entry.Open();

            Store(files, name, entry.Length, data);
        }
    }

    private static bool Admit(Dictionary<string, byte[]> files, List<string> warnings, string name, long length)
    {
        if (name.Length == 0) return false;

        if (length <= MaxFileBytes && files.Values.Sum(f => (long)f.Length) + length <= MaxTotalBytes) return true;

        warnings.Add($"'{name}' ({length} bytes) was not read from the package because it is larger than the {MaxFileBytes} byte file limit or would take the files read past {MaxTotalBytes} bytes");

        return false;
    }

    private static void Store(Dictionary<string, byte[]> files, string name, long length, Stream data)
    {
        if (files.ContainsKey(name))
            Serilog.Log.Warning("[Deploy] Package archive has several entries that map to the file name {FileName}; the last one is used", name);

        using var buffer = new MemoryStream((int)length);

        data.CopyTo(buffer);

        files[name] = buffer.ToArray();
    }
}
