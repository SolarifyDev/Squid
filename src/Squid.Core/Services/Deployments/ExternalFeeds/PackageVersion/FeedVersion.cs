using System.Text.RegularExpressions;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

/// <summary>
/// The one version value the feed version sort, the "is this version sortable" check and the channel version rules share:
/// <c>[v]major.minor[.patch[.revision]][-prerelease]</c>, each number up to <see cref="long"/> (a value that does not fit is not a version here, it never throws).
/// <see cref="CompareTo"/> is the full order (SemVer 2.0 pre-release precedence); <see cref="CompareNumeric"/> compares the numbers only, which is what a channel version range means.
/// </summary>
internal sealed partial record FeedVersion(long Major, long Minor, long Patch, long Revision, string PreRelease) : IComparable<FeedVersion>
{
    public bool IsPreRelease => PreRelease != null;

    public static bool TryParse(string version, out FeedVersion parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(version)) return false;

        var text = version.Length > 1 && version[0] is 'v' or 'V' ? version[1..] : version;
        var match = Pattern().Match(text);

        if (!match.Success) return false;
        if (!TryNumber(match, "major", out var major) || !TryNumber(match, "minor", out var minor)) return false;
        if (!TryNumber(match, "patch", out var patch) || !TryNumber(match, "revision", out var revision)) return false;

        parsed = new FeedVersion(major, minor, patch, revision, match.Groups["pre"].Success ? match.Groups["pre"].Value : null);

        return true;
    }

    public int CompareNumeric(FeedVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);

        return cmp != 0 ? cmp : Revision.CompareTo(other.Revision);
    }

    public int CompareTo(FeedVersion other)
    {
        if (other == null) return 1;

        var cmp = CompareNumeric(other);
        if (cmp != 0) return cmp;

        if (!IsPreRelease || !other.IsPreRelease) return IsPreRelease == other.IsPreRelease ? 0 : IsPreRelease ? -1 : 1;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    // SemVer 2.0 section 11: identifiers left to right; numbers compare numerically and rank below text; text compares as ASCII; with equal prefixes the longer list wins.
    private static int ComparePreRelease(string left, string right)
    {
        var l = left.Split('.');
        var r = right.Split('.');

        for (var i = 0; i < Math.Min(l.Length, r.Length); i++)
        {
            var cmp = CompareIdentifier(l[i], r[i]);
            if (cmp != 0) return cmp;
        }

        return l.Length.CompareTo(r.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);

        if (leftNumeric && rightNumeric) return CompareNumbers(left, right);
        if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    // Digit strings of any length: after dropping leading zeros the longer one is larger, equal lengths compare as text.
    private static int CompareNumbers(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');

        return left.Length != right.Length ? left.Length.CompareTo(right.Length) : Math.Sign(string.CompareOrdinal(left, right));
    }

    private static bool IsNumeric(string identifier) => identifier.Length > 0 && identifier.All(c => c is >= '0' and <= '9');

    private static bool TryNumber(Match match, string group, out long value)
    {
        value = 0;

        return !match.Groups[group].Success || long.TryParse(match.Groups[group].Value, out value);
    }

    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)(\.(?<patch>\d+)(\.(?<revision>\d+))?)?(-(?<pre>.+))?$")]
    private static partial Regex Pattern();
}
