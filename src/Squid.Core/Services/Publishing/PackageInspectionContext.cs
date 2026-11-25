using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>
/// What an inspector gets: the package's facts so far (its file list and the effective root are already in), and bounded access to its files. Paths an
/// inspector passes are relative to the effective root. A file bigger than <see cref="PackageArchiveTextReader.MaxTextBytes"/> is never read, and is
/// recorded in the <see cref="PackageFactKeys.FilesOversized"/> fact.
/// </summary>
public sealed class PackageInspectionContext
{
    private readonly PackageArchiveTextReader _reader;
    private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _oversized = new(StringComparer.Ordinal);

    public PackageInspectionContext(PackageArchiveTextReader reader, PackageArchiveListing listing)
    {
        _reader = reader;

        foreach (var entry in listing.Entries) _sizes.TryAdd(entry.Path, entry.Length);

        Facts = PackageFactsBuilder.FromListing(listing);
    }

    public PackageFacts Facts { get; }

    public bool HasFile(string relativePath) => Facts.HasFile(relativePath);

    public void Set(string key, string value) => Facts.Values[key] = value;

    public void SetTrue(string key) => Facts.Values[key] = PackageFactKeys.True;

    /// <summary>The file's text, or null when it is absent or too big to read.</summary>
    public async Task<string> TryReadTextAsync(string relativePath, CancellationToken ct)
    {
        var path = Facts.RootPrefix() + relativePath;

        if (!_sizes.TryGetValue(path, out var length)) return null;

        if (length > PackageArchiveTextReader.MaxTextBytes) return RecordOversized(path);

        return await _reader.TryReadTextFileAsync(path, ct).ConfigureAwait(false);
    }

    private string RecordOversized(string path)
    {
        _oversized.Add(path);

        Facts.Values[PackageFactKeys.FilesOversized] = string.Join(", ", _oversized);

        return null;
    }
}
