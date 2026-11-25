using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Packages;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Called once per file of an archive, in archive order: its normalised path, its declared size, and a way to read it as text (null when it is bigger than the cap).</summary>
public delegate void PackageArchiveFileVisitor(string path, long declaredLength, Func<string> readText);

/// <summary>Reads manifests (small text files) out of a pushed archive on disk, for the version tokens that read a version from the package itself, and walks its files for inspection.</summary>
public sealed class PackageArchiveTextReader(string path, BuiltInPackageFormat format)
{
    /// <summary>A manifest bigger than this is treated as missing: manifests are small, and nothing here should inflate a bomb into memory.</summary>
    public const int MaxTextBytes = 1024 * 1024;

    public Task<string> TryReadTextFileAsync(string relativePath, CancellationToken ct)
    {
        var wanted = Normalize(relativePath);

        return Task.FromResult(format == BuiltInPackageFormat.Zip ? ReadFromZip(wanted, ct) : ReadFromTar(wanted, ct));
    }

    public Task<IReadOnlyList<string>> ListRootFileNamesAsync(CancellationToken ct)
    {
        IReadOnlyList<string> names = format == BuiltInPackageFormat.Zip ? ListZipRoot() : ListTarRoot();

        return Task.FromResult(names);
    }

    /// <summary>
    /// One pass over every file in the archive, calling <paramref name="visit"/> for each. A file's text is only read when the visitor asks for it, and never more than
    /// <see cref="MaxTextBytes"/> of it. Stops at once when <paramref name="ct"/> is cancelled.
    /// </summary>
    public Task WalkFilesAsync(PackageArchiveFileVisitor visit, CancellationToken ct)
    {
        if (format == BuiltInPackageFormat.Zip) WalkZip(visit, ct);
        else WalkTar(visit, ct);

        return Task.CompletedTask;
    }

    private void WalkZip(PackageArchiveFileVisitor visit, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var name = Normalize(entry.FullName);

            if (entry.Name.Length > 0 && name.Length > 0) visit(name, entry.Length, () => ReadZipEntry(entry));
        }
    }

    private void WalkTar(PackageArchiveFileVisitor visit, CancellationToken ct)
    {
        using var reader = OpenTar();

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();

            var name = Normalize(entry.Name);

            if (IsFile(entry) && name.Length > 0) visit(name, entry.Length, () => ReadTarEntry(entry));

            SkipData(entry);
        }
    }

    private string ReadFromZip(string wanted, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);

        var entry = zip.Entries.FirstOrDefault(e => e.Name.Length > 0 && Normalize(e.FullName) == wanted);

        ct.ThrowIfCancellationRequested();

        return entry == null ? null : ReadZipEntry(entry);
    }

    private string ReadFromTar(string wanted, CancellationToken ct)
    {
        using var reader = OpenTar();

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsFile(entry) || Normalize(entry.Name) != wanted)
            {
                SkipData(entry);
                continue;
            }

            return ReadTarEntry(entry);
        }

        return null;
    }

    private static string ReadZipEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxTextBytes) return null;

        using var stream = entry.Open();

        return ReadBoundedText(stream);
    }

    private static string ReadTarEntry(TarEntry entry) => entry.Length > MaxTextBytes || entry.DataStream == null ? null : ReadBoundedText(entry.DataStream);

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

    // Reads at most MaxTextBytes, whatever the entry header declared: a header that understates its size must not let a bomb into memory. Null when it is bigger.
    // Does not dispose the stream: a tar entry's data stream is still needed to skip past the entry.
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

        buffer.Position = 0;

        using var reader = new StreamReader(buffer, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
