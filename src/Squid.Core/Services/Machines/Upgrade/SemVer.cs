using System.Text.RegularExpressions;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Strict <a href="https://semver.org">semver 2.0.0</a> value type used as the
/// single gate every Tentacle version flows through before reaching the bash
/// template, the Halibut RPC, or a download URL. Four jobs:
///
/// <list type="number">
///   <item><b>Boundary sanitisation (audit H-5)</b> — operator-supplied
///         <c>TargetVersion</c> AND Docker Hub tag names both pass through
///         <see cref="TryParse"/>; anything containing shell metacharacters
///         (`<c>";rm -rf /;#"</c>`, `<c>$(curl …)</c>`, …) is rejected before
///         it can reach the embedded bash template.</item>
///   <item><b>Pre-release support (audit H-3)</b> — <c>System.Version</c>
///         silently drops <c>"2.0.0-beta.1"</c> as unparseable; this regex
///         accepts it so canary tags can be auto-resolved.</item>
///   <item><b>Strict 3-component (audit H-4)</b> — <c>System.Version</c>
///         tolerates 2- or 4-component strings (<c>"1.4"</c> → <c>1.4.0.0</c>);
///         this regex rejects them so <c>squid-tentacle-1.4-linux-x64.tar.gz</c>
///         (a URL that doesn't exist on GitHub Releases) is impossible.</item>
///   <item><b>Spec §11 pre-release precedence (audit N-2)</b> — naive
///         <c>string.CompareOrdinal</c> picks <c>beta.2</c> over <c>beta.11</c>
///         (because <c>'1' &lt; '2'</c>). Spec requires identifier-by-identifier
///         compare with numeric-vs-alphanumeric distinction. Implemented
///         without parsing via the "(length, then lex)" trick on all-digit
///         identifiers — correct up to arbitrary length, no overflow risk.</item>
/// </list>
///
/// Equality (<see cref="Equals(SemVer)"/>) follows §10: build metadata is
/// IGNORED — <c>1.4.0+sha.abc</c> equals <c>1.4.0+sha.xyz</c>.
/// </summary>
internal sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    // Anchored, no leading 'v'. Build metadata `+xxx` is allowed but ignored
    // for comparison (per semver §10).
    //
    // Components disallow leading zeros per §2 (so "01.4.0" rejected) but
    // we accept "0.0.1". Pre-release identifiers per §9: dot-separated,
    // alphanumeric or hyphen.
    private static readonly Regex Pattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string PreRelease { get; }

    /// <summary>The original string (post-trim) — used by callers to round-trip the version into URLs and the bash template.</summary>
    public string Raw { get; }

    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    private SemVer(string raw, int major, int minor, int patch, string preRelease)
    {
        Raw = raw;
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static bool TryParse(string raw, out SemVer parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        var match = Pattern.Match(trimmed);

        if (!match.Success) return false;

        parsed = new SemVer(
            trimmed,
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value),
            match.Groups[4].Success ? match.Groups[4].Value : null);

        return true;
    }

    public static bool IsValid(string raw) => TryParse(raw, out _);

    public int CompareTo(SemVer other)
    {
        if (other == null) return 1;

        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;

        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;

        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // Per semver §11: a normal version sorts HIGHER than its pre-release.
        // 1.4.0 > 1.4.0-beta.1.
        if (!IsPreRelease && other.IsPreRelease) return 1;
        if (IsPreRelease && !other.IsPreRelease) return -1;
        if (!IsPreRelease && !other.IsPreRelease) return 0;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Spec §11 pre-release precedence: walk dot-separated identifiers left
    /// to right, applying the four sub-rules at each position.
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var min = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < min; i++)
        {
            var c = ComparePreReleaseIdentifier(leftParts[i], rightParts[i]);

            if (c != 0) return c;
        }

        // §11.4 — equal up to common-prefix length: longer set wins.
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftIsNumeric = IsAllDigits(left);
        var rightIsNumeric = IsAllDigits(right);

        // §11.3: numeric identifiers have LOWER precedence than alphanumeric
        // at the same dot position. `1` < `alpha`, `99` < `a`.
        if (leftIsNumeric && !rightIsNumeric) return -1;
        if (!leftIsNumeric && rightIsNumeric) return 1;

        // §11.1: both numeric → numeric compare. Trick — pre-release numeric
        // identifiers are forbidden leading zeros per §9 (regex enforces),
        // so "(length, then lex)" is exactly numeric compare AND immune to
        // overflow on arbitrary-length numerics ("99999999999999999999"
        // would blow long.TryParse but works fine here).
        if (leftIsNumeric)
        {
            var lenCompare = left.Length.CompareTo(right.Length);
            return lenCompare != 0 ? lenCompare : string.CompareOrdinal(left, right);
        }

        // §11.2: both alphanumeric → ASCII lex compare.
        return string.CompareOrdinal(left, right);
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;

        foreach (var c in s)
            if (c < '0' || c > '9')
                return false;

        return true;
    }

    // ── Equality (§10: build metadata IGNORED) ──────────────────────────────

    public bool Equals(SemVer other) => other != null && CompareTo(other) == 0;

    public override bool Equals(object obj) => obj is SemVer s && Equals(s);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public static bool operator ==(SemVer left, SemVer right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    public static bool operator !=(SemVer left, SemVer right) => !(left == right);

    public override string ToString() => Raw;
}
