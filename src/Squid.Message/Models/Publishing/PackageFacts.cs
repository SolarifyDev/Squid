namespace Squid.Message.Models.Publishing;

/// <summary>
/// What inspectors learned about a pushed package: every file path (normalised: <c>/</c> separated, no leading <c>./</c>) and a dictionary of typed facts
/// (<see cref="PackageFactKeys"/>). Paths are as stored in the archive; when the whole archive sits in one top-level folder, the <c>HasFile</c> and
/// <c>RelativeFiles</c> members read paths relative to that folder, the effective root.
/// </summary>
public class PackageFacts
{
    public List<string> Files { get; set; } = new();

    public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>The effective root with a trailing slash, or empty when the archive is not wrapped in a single folder.</summary>
    public string RootPrefix() => Values.TryGetValue(PackageFactKeys.ArchiveRoot, out var root) && !string.IsNullOrEmpty(root) ? root + "/" : string.Empty;

    public string Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public bool Is(string key) => Get(key) == PackageFactKeys.True;

    public bool HasFile(string relativePath) => Files.Contains(RootPrefix() + relativePath, StringComparer.Ordinal);

    public IEnumerable<string> RelativeFiles()
    {
        var prefix = RootPrefix();

        return Files.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).Select(f => f[prefix.Length..]);
    }
}
