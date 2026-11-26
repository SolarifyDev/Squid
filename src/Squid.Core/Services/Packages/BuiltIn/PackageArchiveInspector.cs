using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Publishing;
using Squid.Message.Constants;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>What one walk over a pushed archive learned: the content fingerprint, the files at the root, the small root manifests the version tokens read, and, when a scanner was given, what package inspection needs.</summary>
internal sealed record PackageArchiveInspection(string ContentFingerprint, IReadOnlyList<string> RootFileNames, IReadOnlyDictionary<string, string> RootManifests, PackageArchiveScan Scan = null);

internal sealed record ArchiveLimits(int MaxEntries, long MaxUncompressedBytes, int MaxCompressionRatio, long CompressionRatioFloorBytes)
{
    public static ArchiveLimits Default { get; } = new(BuiltInFeed.MaxArchiveEntries, BuiltInFeed.MaxUncompressedBytes, BuiltInFeed.MaxCompressionRatio, BuiltInFeed.CompressionRatioFloorBytes);
}

/// <summary>
/// The one pass over a pushed archive: refuses unsafe entry names (the shared <see cref="PackageArchiveSafety"/> predicate), enforces the decompression limits as it
/// reads, and while reading every file computes what the later steps need, so a tar.gz is not unpacked again for each of them. The limits are checked on the bytes
/// actually unpacked (not on sizes an archive declares), and the ratio as soon as the floor is passed, so a bomb is refused after a fraction of its size.
/// </summary>
internal static class PackageArchiveInspector
{
    private const int BufferSize = 81920;

    /// <summary>The root files whose text the walk keeps: the manifests the version tokens read.</summary>
    public static bool IsRootManifest(string name) => !name.Contains('/') && (ManifestNames.Contains(name) || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> ManifestNames = new(StringComparer.OrdinalIgnoreCase) { "package.json", "pyproject.toml" };

    public static PackageArchiveInspection Inspect(string path, BuiltInPackageFormat format, string packageId, ArchiveLimits limits, PackageArchiveScanner scanner = null, CancellationToken ct = default)
    {
        var walk = new Walk(new FileInfo(path).Length, packageId, limits, scanner, ct);

        try
        {
            if (format == BuiltInPackageFormat.Zip) WalkZip(path, walk);
            else WalkTarGz(path, walk);
        }
        catch (Exception ex) when (ex is InvalidOperationException and not PackageArchiveLimitExceededException || ex is InvalidDataException or IOException)
        {
            throw new PackageArchiveRejectedException(ex.Message, ex);
        }

        return walk.Finish();
    }

    private static void WalkZip(string path, Walk walk)
    {
        using var zip = ZipFile.OpenRead(path);

        walk.CountEntries(zip.Entries.Count);

        foreach (var entry in zip.Entries)
        {
            walk.CheckName(entry.FullName);

            if (entry.Name.Length == 0) continue;

            using var stream = entry.Open();

            walk.ReadFile(entry.FullName, stream, entry.Length);
        }
    }

    private static void WalkTarGz(string path, Walk walk)
    {
        using var reader = new TarReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), leaveOpen: false);

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            walk.CountEntries(1);
            walk.CheckName(entry.Name);

            // The gzip stream cannot seek: an entry's data has to be read past (and is counted) before the next entry, or the reader returns garbage.
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream != null) walk.ReadFile(entry.Name, entry.DataStream, entry.Length);
            else walk.Discard(entry.DataStream);
        }
    }

    private sealed class Walk(long archiveBytes, string packageId, ArchiveLimits limits, PackageArchiveScanner scanner, CancellationToken ct)
    {
        private readonly List<(string Path, string Hash)> _files = new();
        private readonly List<string> _root = new();
        private readonly Dictionary<string, string> _manifests = new();
        private int _entries;
        private long _unpacked;

        public void CountEntries(int count)
        {
            ct.ThrowIfCancellationRequested();

            _entries += count;

            if (_entries > limits.MaxEntries)
                throw new PackageArchiveLimitExceededException($"has more than {limits.MaxEntries} entries");
        }

        public void CheckName(string entryName)
        {
            ct.ThrowIfCancellationRequested();

            PackageArchiveSafety.RejectIfUnsafe(entryName, packageId, "being pushed");
        }

        public void ReadFile(string entryName, Stream data, long declaredLength)
        {
            var name = PackageArchiveSafety.StripLeadingCurrentDirectory(entryName);
            var scanWantsText = scanner?.Observe(name) ?? false;
            var keep = scanWantsText || PackageArchiveInspector.IsRootManifest(name) ? new CappedBuffer(PackageArchiveTextReader.MaxTextBytes) : null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            Pump(data, hash, keep);

            _files.Add((name, Convert.ToHexString(hash.GetHashAndReset())));

            if (!name.Contains('/')) _root.Add(name);

            var text = keep == null || keep.Overflowed ? null : PackageArchiveTextReader.DecodeText(keep.ToArray());

            if (text != null && IsRootManifest(name)) _manifests[name] = text;
            if (scanWantsText) scanner.Captured(name, text);
        }

        public void Discard(Stream data)
        {
            if (data != null) Pump(data, null, null);
        }

        public PackageArchiveInspection Finish() => new(Fingerprint(), _root, _manifests, scanner?.Finish());

        private void Pump(Stream data, IncrementalHash hash, CappedBuffer keep)
        {
            var buffer = new byte[BufferSize];
            int read;

            while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                Count(read);
                hash?.AppendData(buffer, 0, read);
                keep?.Append(buffer, read);
            }
        }

        private void Count(int read)
        {
            _unpacked += read;

            if (_unpacked > limits.MaxUncompressedBytes)
                throw new PackageArchiveLimitExceededException($"unpacks to more than {limits.MaxUncompressedBytes} bytes");

            if (_unpacked > limits.CompressionRatioFloorBytes && _unpacked / Math.Max(archiveBytes, 1) > limits.MaxCompressionRatio)
                throw new PackageArchiveLimitExceededException($"unpacks to more than {limits.MaxCompressionRatio} times its size");
        }

        // Keeps the first limit bytes of a file and notes it when there was more, while the walk goes on counting and hashing the rest.
        private sealed class CappedBuffer(int limit)
        {
            private readonly MemoryStream _bytes = new();

            public bool Overflowed { get; private set; }

            public void Append(byte[] buffer, int count)
            {
                if (Overflowed) return;

                if (_bytes.Length + count > limit)
                {
                    Overflowed = true;
                    _bytes.SetLength(0);
                    return;
                }

                _bytes.Write(buffer, 0, count);
            }

            public byte[] ToArray() => _bytes.ToArray();
        }

        // The same files give the same fingerprint however they were packed: it covers each file's normalised path and content hash in path order, and nothing else
        // (not directories, order, timestamps, permissions or the archive format).
        private string Fingerprint()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            foreach (var (path, fileHash) in _files.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Hash, StringComparer.Ordinal))
                hash.AppendData(Encoding.UTF8.GetBytes($"{path}\0{fileHash}\n"));

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

    }
}
