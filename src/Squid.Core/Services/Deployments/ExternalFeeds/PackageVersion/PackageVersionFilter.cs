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

    internal static List<string> SortByVersionDescending(IEnumerable<string> versions)
    {
        return versions
            .Select(v => (Original: v, Parsed: TryParseVersion(v)))
            .OrderByDescending(x => x.Parsed.Major)
            .ThenByDescending(x => x.Parsed.Minor)
            .ThenByDescending(x => x.Parsed.Patch)
            .ThenByDescending(x => x.Parsed.IsRelease)
            .ThenBy(x => x.Parsed.PreRelease, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Original)
            .ToList();
    }

    private static ParsedVersion TryParseVersion(string version)
    {
        var v = version;

        if (v.Length > 1 && v[0] is 'v' or 'V')
            v = v[1..];

        var match = VersionParsePattern().Match(v);

        if (!match.Success)
            return new ParsedVersion(-1, -1, -1, false, version);

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        var preRelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;

        return new ParsedVersion(major, minor, patch, preRelease == null, preRelease ?? "");
    }

    private readonly record struct ParsedVersion(int Major, int Minor, int Patch, bool IsRelease, string PreRelease);

    // Matches: digits.digits[.digits][.digits]-anything
    [GeneratedRegex(@"^\d+\.\d+(\.\d+){0,2}-.+$")]
    private static partial Regex PreReleasePattern();

    // Parses: [v]major.minor[.patch][-prerelease]
    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)(\.(?<patch>\d+))?(-(?<pre>.+))?$")]
    private static partial Regex VersionParsePattern();
}
