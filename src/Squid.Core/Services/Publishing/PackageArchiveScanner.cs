using Squid.Core.Services.Packages.BuiltIn;

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
/// Walks every file of an archive once (the push already read it all to check it is safe, and names are cheap), keeping only what is bounded: a capped list of names,
/// the paths of the probed files, and the text of the small files to read. A probed or read path is matched at the archive's top level and inside its first
/// top-level folder, which is the only folder that can turn out to be the effective root.
/// </summary>
public static class PackageArchiveScanner
{
    public const int MaxRetainedNames = 5000;

    // macOS's zip puts a resource-fork copy of every file under this folder, next to the real one; it is not content and must not count or hide a wrapper folder.
    private const string MacResourceForkFolder = "__MACOSX/";

    public static async Task<PackageArchiveScan> ScanAsync(PackageArchiveTextReader reader, IReadOnlySet<string> probes, IReadOnlySet<string> reads, CancellationToken ct)
    {
        var scan = new PackageArchiveScan { Reads = reads };
        var state = new RootState();

        await reader.WalkFilesAsync((path, _, readText) => Visit(scan, state, probes, reads, path, readText), ct).ConfigureAwait(false);

        scan.Root = state.Root();

        return scan;
    }

    private static void Visit(PackageArchiveScan scan, RootState state, IReadOnlySet<string> probes, IReadOnlySet<string> reads, string path, Func<string> readText)
    {
        if (path.StartsWith(MacResourceForkFolder, StringComparison.Ordinal)) return;

        scan.Count++;

        if (scan.Names.Count < MaxRetainedNames) scan.Names.Add(path);

        state.Observe(path);

        if (Matches(state, probes, path)) scan.Present.Add(path);

        if (Matches(state, reads, path)) Read(scan, path, readText);
    }

    private static bool Matches(RootState state, IReadOnlySet<string> wanted, string path)
    {
        if (wanted.Count == 0) return false;

        return wanted.Contains(path) || (state.FirstFolder != null && path.StartsWith(state.FirstFolder + "/", StringComparison.Ordinal) && wanted.Contains(path[(state.FirstFolder.Length + 1)..]));
    }

    private static void Read(PackageArchiveScan scan, string path, Func<string> readText)
    {
        if (scan.Texts.ContainsKey(path) || scan.Oversized.Contains(path)) return;

        var text = readText();

        if (text == null) scan.Oversized.Add(path);
        else scan.Texts[path] = text;
    }

    // Tracks, as files go by, whether they all sit under one folder: a file at the top, or a second folder, means they do not.
    private sealed class RootState
    {
        private bool _hasTopLevelFile;
        private bool _hasSecondFolder;

        public string FirstFolder { get; private set; }

        public void Observe(string path)
        {
            var slash = path.IndexOf('/');

            if (slash < 0) _hasTopLevelFile = true;
            else if (FirstFolder == null) FirstFolder = path[..slash];
            else if (!path.StartsWith(FirstFolder + "/", StringComparison.Ordinal)) _hasSecondFolder = true;
        }

        public string Root() => FirstFolder != null && !_hasTopLevelFile && !_hasSecondFolder ? FirstFolder : string.Empty;
    }
}
