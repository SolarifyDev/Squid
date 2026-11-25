namespace Squid.Core.Services.Publishing;

/// <summary>
/// What one pass over an archive found. <see cref="Count"/> is every file; only the first <see cref="PackageArchiveScanner.MaxRetainedNames"/> names are kept.
/// <see cref="Root"/>, <see cref="Present"/> and <see cref="Texts"/> are computed from every file, so they do not depend on where in the archive a file sits.
/// </summary>
public sealed class PackageArchiveScan
{
    public List<string> Names { get; } = new();

    public int Count { get; set; }

    /// <summary>The single top-level folder every file sits under, or empty.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Full paths of the files that match a probe, whether or not the archive is wrapped in a folder.</summary>
    public HashSet<string> Present { get; } = new(StringComparer.Ordinal);

    /// <summary>The text of the files that match a read, by full path.</summary>
    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    /// <summary>Full paths of matching files too big to read.</summary>
    public HashSet<string> Oversized { get; } = new(StringComparer.Ordinal);

    /// <summary>The relative paths the scan was asked to read, so a later read of anything else is known to be a different request.</summary>
    public IReadOnlySet<string> Reads { get; init; } = new HashSet<string>();
}

/// <summary>
/// Collects what package inspection needs from the files of an archive as another walk goes over them (the push's safety walk, which reads every file anyway): a
/// capped list of names, the paths of the probed files, and the text of the small files to read. A probed or read path is matched at the archive's top level and inside
/// its first top-level folder, which is the only folder that can turn out to be the effective root. The walk calls <see cref="Observe"/> for each file before it
/// reads it, and <see cref="Captured"/> with the text when <see cref="Observe"/> asked for it.
/// </summary>
public sealed class PackageArchiveScanner(IReadOnlySet<string> probes, IReadOnlySet<string> reads)
{
    public const int MaxRetainedNames = 5000;

    // macOS's zip puts a resource-fork copy of every file under this folder, next to the real one; it is not content and must not count or hide a wrapper folder.
    private const string MacResourceForkFolder = "__MACOSX/";

    private readonly PackageArchiveScan _scan = new() { Reads = reads };
    private bool _hasTopLevelFile;
    private bool _hasSecondFolder;
    private string _firstFolder;

    /// <summary>Takes note of one file. True when its text is wanted: the caller then passes it to <see cref="Captured"/>.</summary>
    public bool Observe(string path)
    {
        if (path.StartsWith(MacResourceForkFolder, StringComparison.Ordinal)) return false;

        _scan.Count++;

        if (_scan.Names.Count < MaxRetainedNames) _scan.Names.Add(path);

        TrackRoot(path);

        if (Matches(probes, path) || Matches(reads, path)) _scan.Present.Add(path);

        return Matches(reads, path) && !_scan.Texts.ContainsKey(path) && !_scan.Oversized.Contains(path);
    }

    /// <summary>The text of a file <see cref="Observe"/> asked for, or null when it was too big to keep.</summary>
    public void Captured(string path, string text)
    {
        if (text == null) _scan.Oversized.Add(path);
        else _scan.Texts[path] = text;
    }

    public PackageArchiveScan Finish()
    {
        _scan.Root = _firstFolder != null && !_hasTopLevelFile && !_hasSecondFolder ? _firstFolder : string.Empty;

        return _scan;
    }

    // Tracks, as files go by, whether they all sit under one folder: a file at the top, or a second folder, means they do not.
    private void TrackRoot(string path)
    {
        var slash = path.IndexOf('/');

        if (slash < 0) _hasTopLevelFile = true;
        else if (_firstFolder == null) _firstFolder = path[..slash];
        else if (!path.StartsWith(_firstFolder + "/", StringComparison.Ordinal)) _hasSecondFolder = true;
    }

    private bool Matches(IReadOnlySet<string> wanted, string path)
    {
        if (wanted.Count == 0) return false;

        return wanted.Contains(path) || (_firstFolder != null && path.StartsWith(_firstFolder + "/", StringComparison.Ordinal) && wanted.Contains(path[(_firstFolder.Length + 1)..]));
    }
}
