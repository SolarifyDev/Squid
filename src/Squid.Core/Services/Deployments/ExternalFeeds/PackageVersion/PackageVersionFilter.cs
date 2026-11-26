using System.Text.RegularExpressions;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

public static partial class PackageVersionFilter
{
    internal static List<string> Apply(List<string> versions, bool includePreRelease, string filter, int take)
    {
        IEnumerable<string> result = versions;

        if (!includePreRelease)
            result = result.Where(v => !IsPreRelease(v));

        if (!string.IsNullOrWhiteSpace(filter))
            result = result.Where(v => v.Contains(filter, StringComparison.OrdinalIgnoreCase));

        return SortByVersionDescending(result).Take(take).ToList();
    }

    internal static bool IsPreRelease(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        var v = version;

        if (v.Length > 1 && v[0] is 'v' or 'V')
            v = v[1..];

        return PreReleasePattern().IsMatch(v);
    }

    /// <summary>Highest first (SemVer 2.0 precedence, see <see cref="FeedVersion"/>); what does not parse as a version comes last, in text order.</summary>
    internal static List<string> SortByVersionDescending(IEnumerable<string> versions)
    {
        var parsed = versions.Select(v => (Original: v, Version: FeedVersion.TryParse(v, out var p) ? p : null)).ToList();

        var sortable = parsed.Where(x => x.Version != null).OrderByDescending(x => x.Version).Select(x => x.Original);
        var unparsed = parsed.Where(x => x.Version == null).Select(x => x.Original).OrderBy(v => v, StringComparer.OrdinalIgnoreCase);

        return sortable.Concat(unparsed).ToList();
    }

    /// <summary>True when <paramref name="version"/> parses as a <see cref="FeedVersion"/>, so it lands in its true position instead of at the bottom.</summary>
    internal static bool IsSortable(string version) => FeedVersion.TryParse(version, out _);

    // Matches: digits.digits[.digits][.digits]-anything
    [GeneratedRegex(@"^\d+\.\d+(\.\d+){0,2}-.+$")]
    private static partial Regex PreReleasePattern();
}
