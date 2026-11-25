using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Packages;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Reads manifests (small text files) out of a pushed archive on disk, for the version tokens that read a version from the package itself.</summary>
internal sealed class PackageArchiveTextReader(string path, BuiltInPackageFormat format, PackageArchiveInspection inspection = null)
{
    /// <summary>A manifest bigger than this is treated as missing: manifests are small, and nothing here should inflate a bomb into memory.</summary>
    public const int MaxTextBytes = 1024 * 1024;

    public Task<string> TryReadTextFileAsync(string relativePath, CancellationToken ct)
    {
        var wanted = Normalize(relativePath);

        ct.ThrowIfCancellationRequested();

        // The push walk has already seen every root manifest (or found there is none), so asking again would unpack the archive again for nothing.
        if (inspection != null && PackageArchiveInspector.IsRootManifest(wanted))
            return Task.FromResult(inspection.RootManifests.GetValueOrDefault(wanted));

        return Task.FromResult(format == BuiltInPackageFormat.Zip ? ReadFromZip(wanted) : ReadFromTar(wanted));
    }

    public Task<IReadOnlyList<string>> ListRootFileNamesAsync(CancellationToken ct)
    {
        if (inspection != null) return Task.FromResult(inspection.RootFileNames);

        IReadOnlyList<string> names = format == BuiltInPackageFormat.Zip ? ListZipRoot() : ListTarRoot();

        return Task.FromResult(names);
    }

    private string ReadFromZip(string wanted)
    {
        using var zip = ZipFile.OpenRead(path);

        var entry = zip.Entries.FirstOrDefault(e => e.Name.Length > 0 && Normalize(e.FullName) == wanted);

        return entry == null || entry.Length > MaxTextBytes ? null : ReadZipEntry(entry);
    }

    private string ReadFromTar(string wanted)
    {
        using var reader = OpenTar();

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (!IsFile(entry) || Normalize(entry.Name) != wanted)
            {
                SkipData(entry);
                continue;
            }

            return entry.Length > MaxTextBytes || entry.DataStream == null ? null : ReadBoundedText(entry.DataStream);
        }

        return null;
    }

    private List<string> ListZipRoot()
    {
        using var zip = ZipFile.OpenRead(path);

        return zip.Entries.Where(e => e.Name.Length > 0).Select(e => Normalize(e.FullName)).Where(IsRoot).ToList();
    }

    private List<string> ListTarRoot()
    {
        using var reader = OpenTar();

        var names = new List<string>();

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (IsFile(entry) && IsRoot(Normalize(entry.Name))) names.Add(Normalize(entry.Name));

            SkipData(entry);
        }

        return names;
    }

    private TarReader OpenTar() => new(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), leaveOpen: false);

    // The gzip stream cannot seek, so an entry's data must be read past before the next entry: the reader does not skip it, and would hand back garbage.
    private static void SkipData(TarEntry entry) => entry.DataStream?.CopyTo(Stream.Null);

    private static bool IsFile(TarEntry entry) => entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;

    private static bool IsRoot(string name) => name.Length > 0 && !name.Contains('/');

    private static string Normalize(string name) => PackageArchiveSafety.StripLeadingCurrentDirectory(name);

    private static string ReadZipEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();

        return ReadBoundedText(stream);
    }

    /// <summary>
    /// Reads at most <see cref="MaxTextBytes"/>, whatever the entry header declared. Null when there is more. Does not dispose the stream: a tar entry's data stream is
    /// still needed to skip past the entry.
    /// </summary>
    public static string ReadBoundedText(Stream input)
    {
        using var buffer = new MemoryStream();

        var chunk = new byte[8192];
        int read;

        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > MaxTextBytes) return null;
        }

        return DecodeText(buffer.ToArray());
    }

    /// <summary>UTF-8 text with a leading byte order mark dropped.</summary>
    public static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
