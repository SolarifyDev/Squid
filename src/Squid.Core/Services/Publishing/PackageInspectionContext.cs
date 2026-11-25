using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>
/// What an inspector gets: the package's facts so far (the effective root and file count are already in), and bounded access to its files. Paths an inspector
/// passes are relative to the effective root. Files an inspector declared in <see cref="IPackageInspector.FilesToProbe"/> and
/// <see cref="IPackageInspector.FilesToRead"/> were found and read in the one pass over the archive; anything else is looked up in the retained names, or read
/// with a pass of its own. A file bigger than <see cref="PackageArchiveTextReader.MaxTextBytes"/> is never read, and is recorded in the
/// <see cref="PackageFactKeys.FilesOversized"/> fact. Every fact value is cut to <see cref="MaxValueLength"/> characters.
/// </summary>
public sealed class PackageInspectionContext
{
    public const int MaxValueLength = 512;

    public const string TruncationMarker = "...[truncated]";

    private readonly PackageArchiveTextReader _reader;
    private readonly PackageArchiveScan _scan;
    private readonly SortedSet<string> _oversized = new(StringComparer.Ordinal);

    public PackageInspectionContext(PackageArchiveTextReader reader, PackageArchiveScan scan)
    {
        _reader = reader;
        _scan = scan;

        Facts = new PackageFacts();

        Set(PackageFactKeys.ArchiveWrapped, scan.Root.Length > 0 ? PackageFactKeys.True : "false");
        Set(PackageFactKeys.ArchiveRoot, scan.Root);
        Set(PackageFactKeys.FilesCount, scan.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (scan.Count > scan.Names.Count) SetTrue(PackageFactKeys.FilesTruncated);
    }

    public PackageFacts Facts { get; }

    /// <summary>The effective root with a trailing slash, or empty when the archive is not wrapped in a single folder.</summary>
    public string RootPrefix => _scan.Root.Length > 0 ? _scan.Root + "/" : string.Empty;

    /// <summary>The first <see cref="PackageArchiveScanner.MaxRetainedNames"/> file paths, relative to the effective root. Not the whole archive when <see cref="PackageFactKeys.FilesTruncated"/> is set.</summary>
    public IEnumerable<string> RelativeFiles() => _scan.Names.Where(n => n.StartsWith(RootPrefix, StringComparison.Ordinal)).Select(n => n[RootPrefix.Length..]);

    /// <summary>Whether the file exists. Reliable for a file declared in <see cref="IPackageInspector.FilesToProbe"/> or <see cref="IPackageInspector.FilesToRead"/>, wherever it sits in the archive.</summary>
    public bool HasFile(string relativePath)
    {
        var full = RootPrefix + relativePath;

        return _scan.Present.Contains(full) || _scan.Names.Contains(full, StringComparer.Ordinal);
    }

    /// <summary>Records a fact, cut to <see cref="MaxValueLength"/> characters with <see cref="TruncationMarker"/> at the end when it was longer.</summary>
    public void Set(string key, string value) => Facts.Values[key] = Cap(value);

    public void SetTrue(string key) => Facts.Values[key] = PackageFactKeys.True;

    /// <summary>The file's text, or null when it is absent or too big to read.</summary>
    public async Task<string> TryReadTextAsync(string relativePath, CancellationToken ct)
    {
        var path = RootPrefix + relativePath;

        if (_scan.Oversized.Contains(path)) return RecordOversized(path);

        if (_scan.Texts.TryGetValue(path, out var text)) return text;

        if (_scan.Reads.Contains(relativePath)) return null;

        return await _reader.TryReadTextFileAsync(path, ct).ConfigureAwait(false);
    }

    internal static string Cap(string value)
    {
        if (value == null) return string.Empty;

        return value.Length <= MaxValueLength ? value : value[..(MaxValueLength - TruncationMarker.Length)] + TruncationMarker;
    }

    private string RecordOversized(string path)
    {
        _oversized.Add(path);

        Set(PackageFactKeys.FilesOversized, string.Join(", ", _oversized));

        return null;
    }
}
