using System.Text.Json.Serialization;

namespace Squid.Message.Models.Publishing;

/// <summary>
/// What inspectors learned about a pushed package: a dictionary of typed facts (<see cref="PackageFactKeys"/>), which is all that is stored and served, and, while
/// the push runs, the archive's file paths (normalised: <c>/</c> separated, no leading <c>./</c>). The paths are in memory only: they are not serialised, so
/// facts read back from storage have none, and a recipe matches on <see cref="Values"/>. When the whole archive sits in one top-level folder, <c>HasFile</c> and
/// <c>RelativeFiles</c> read paths relative to that folder, the effective root.
/// </summary>
public class PackageFacts
{
    [JsonIgnore]
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
