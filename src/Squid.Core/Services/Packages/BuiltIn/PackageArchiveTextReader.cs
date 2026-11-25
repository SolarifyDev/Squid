using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Packages;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>One file of an archive: its normalised path and its declared uncompressed size.</summary>
public sealed record PackageArchiveEntry(string Path, long Length);

/// <summary>The files of an archive, at most <see cref="PackageArchiveTextReader.MaxListedFiles"/> of them; <c>Truncated</c> says there were more.</summary>
public sealed record PackageArchiveListing(IReadOnlyList<PackageArchiveEntry> Entries, bool Truncated);

/// <summary>Reads manifests (small text files) out of a pushed archive on disk, for the version tokens that read a version from the package itself, and lists its files for inspection.</summary>
public sealed class PackageArchiveTextReader(string path, BuiltInPackageFormat format)
{
    /// <summary>A manifest bigger than this is treated as missing: manifests are small, and nothing here should inflate a bomb into memory.</summary>
    public const int MaxTextBytes = 1024 * 1024;

    /// <summary>A listing stops here, so an archive of millions of tiny files cannot fill memory with names.</summary>
    public const int MaxListedFiles = 5000;

    public Task<string> TryReadTextFileAsync(string relativePath, CancellationToken ct)
    {
        var wanted = Normalize(relativePath);

        return Task.FromResult(format == BuiltInPackageFormat.Zip ? ReadFromZip(wanted) : ReadFromTar(wanted));
    }

    public Task<IReadOnlyList<string>> ListRootFileNamesAsync(CancellationToken ct)
    {
        IReadOnlyList<string> names = format == BuiltInPackageFormat.Zip ? ListZipRoot() : ListTarRoot();

        return Task.FromResult(names);
    }

    public Task<PackageArchiveListing> ListFilesAsync(CancellationToken ct)
    {
        return Task.FromResult(format == BuiltInPackageFormat.Zip ? ListZipFiles() : ListTarFiles());
    }

    private string ReadFromZip(string wanted)
    {
        using var zip = ZipFile.OpenRead(path);

        var entry = zip.Entries.FirstOrDefault(e => e.Name.Length > 0 && Normalize(e.FullName) == wanted);

        return entry == null || entry.Length > MaxTextBytes ? null : ReadText(entry.Open());
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

            return entry.Length > MaxTextBytes || entry.DataStream == null ? null : ReadText(entry.DataStream);
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

    private PackageArchiveListing ListZipFiles()
    {
        using var zip = ZipFile.OpenRead(path);

        var entries = zip.Entries.Where(e => e.Name.Length > 0).Select(e => new PackageArchiveEntry(Normalize(e.FullName), e.Length)).Where(e => e.Path.Length > 0).Take(MaxListedFiles + 1).ToList();

        return Capped(entries);
    }

    private PackageArchiveListing ListTarFiles()
    {
        using var reader = OpenTar();

        var entries = new List<PackageArchiveEntry>();

        while (entries.Count <= MaxListedFiles && reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (IsFile(entry) && Normalize(entry.Name).Length > 0) entries.Add(new PackageArchiveEntry(Normalize(entry.Name), entry.Length));

            SkipData(entry);
        }

        return Capped(entries);
    }

    private static PackageArchiveListing Capped(List<PackageArchiveEntry> entries) => new(entries.Take(MaxListedFiles).ToList(), entries.Count > MaxListedFiles);

    private TarReader OpenTar() => new(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), leaveOpen: false);

    // The gzip stream cannot seek, so an entry's data must be read past before the next entry: the reader does not skip it, and would hand back garbage.
    private static void SkipData(TarEntry entry) => entry.DataStream?.CopyTo(Stream.Null);

    private static bool IsFile(TarEntry entry) => entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;

    private static bool IsRoot(string name) => name.Length > 0 && !name.Contains('/');

    private static string Normalize(string name) => PackageArchiveSafety.StripLeadingCurrentDirectory(name);

    // Reads at most MaxTextBytes, whatever the entry header declared: a header that understates its size must not let a bomb into memory. Null when it is bigger.
    private static string ReadText(Stream stream)
    {
        using var input = stream;
        using var buffer = new MemoryStream();

        var chunk = new byte[8192];
        int read;

        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > MaxTextBytes) return null;
        }

        buffer.Position = 0;

        using var reader = new StreamReader(buffer, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
