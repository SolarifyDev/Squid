using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

/// <summary>The feed sort, IsSortable and the channel version rules all read versions through <see cref="FeedVersion"/>; these tests pin that they cannot disagree.</summary>
public class FeedVersionTests
{
    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("2026.11.25.3", true)]
    [InlineData("1.0.0.20261125143000", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("V1.2", true)]
    [InlineData("1.2.3-rc.1", true)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1", false)]
    [InlineData("v", false)]
    [InlineData("latest", false)]
    [InlineData("99999999999999999999.0.0", false)]
    [InlineData("1.0.0.99999999999999999999", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EveryConsumerAgreesWhetherAVersionParses(string version, bool expected)
    {
        FeedVersion.TryParse(version, out _).ShouldBe(expected);
        PackageVersionFilter.IsSortable(version).ShouldBe(expected);
        ChannelVersionRuleValidator.TryParseVersion(version, out _).ShouldBe(expected);
    }

    [Fact]
    public void BuildStampRevision_NoLongerBreaksTheSortOrTheFilter()
    {
        var versions = new List<string> { "1.0.0.20261125143000", "1.0.0", "1.0.0.20261124090000", "latest" };

        PackageVersionFilter.SortByVersionDescending(versions).ShouldBe(["1.0.0.20261125143000", "1.0.0.20261124090000", "1.0.0", "latest"]);
        PackageVersionFilter.Apply(["99999999999999999999.0.0", "1.0.0"], true, null, 10).ShouldBe(["1.0.0", "99999999999999999999.0.0"]);
    }

    // version, range, whether the channel rule accepts it. An accepted version must also sort at or above the range's lower bound.
    [Theory]
    [InlineData("1.5.0", "[1.0,2.0)", true)]
    [InlineData("2.0.0", "[1.0,2.0)", false)]
    [InlineData("1.0.0", "(1.0,)", false)]
    [InlineData("1.0.1", "(1.0,)", true)]
    [InlineData("2026.11.25.3", "[2026.11.25.2,2026.11.25.5]", true)]
    [InlineData("2026.11.25.9", "[2026.11.25.2,2026.11.25.5]", false)]
    [InlineData("2026.11.25.1", "[2026.11.25.2,2026.11.25.5]", false)]
    [InlineData("v1.5.0", "[1.0,2.0)", true)]
    [InlineData("v9.9.9", "[1.0,2.0)", false)]
    [InlineData("1.0.0.5", "(,1.0]", false)]
    public void ChannelRangeAndSortOrderAgree(string version, string range, bool accepted)
    {
        var rule = new ChannelVersionRule { VersionRange = range };

        ChannelVersionRuleValidator.SatisfiesRule(version, rule).ShouldBe(accepted);

        var bound = range.Trim('[', '(', ']', ')').Split(',')[0];

        if (!accepted || bound.Length == 0) return;

        PackageVersionFilter.SortByVersionDescending([bound, version]).Last().ShouldBe(bound);
    }

    [Fact]
    public void ChannelRange_StillIgnoresThePreReleaseTag_AsBefore()
    {
        // Unchanged: a range compares the numbers only, so a pre-release of the lower bound is inside [1.0.0,2.0). Tag filtering is the PreReleaseTag rule's job.
        ChannelVersionRuleValidator.SatisfiesRule("1.0.0-beta", new ChannelVersionRule { VersionRange = "[1.0.0,2.0)" }).ShouldBeTrue();
    }

    [Fact]
    public void AFiveOrMorePartVersion_IsUnparseable_SoNoRangeRuleJudgesIt()
    {
        // Unchanged contract of the channel rules: a version that cannot be read is not rejected by a range rule.
        ChannelVersionRuleValidator.SatisfiesRule("9.9.9.9.9", new ChannelVersionRule { VersionRange = "[1.0,2.0)" }).ShouldBeTrue();
    }

    [Fact]
    public void PreReleasePrecedence_FollowsTheSemVerSpecExample()
    {
        // semver.org section 11: 1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0
        var ascending = new List<string> { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0" };
        var shuffled = ascending.OrderBy(v => v.GetHashCode() ^ 0x5bd1e995).ToList();
        var reversed = Enumerable.Reverse(ascending).ToList();

        PackageVersionFilter.SortByVersionDescending(shuffled).ShouldBe(reversed);
        PackageVersionFilter.SortByVersionDescending(ascending).ShouldBe(reversed);
    }

    [Theory]
    [InlineData("1.0.0-ci.10", "1.0.0-ci.1")]
    [InlineData("1.0.0-ci.2", "1.0.0-ci.1")]
    [InlineData("1.0.0-ci.1.1", "1.0.0-ci.1")]
    [InlineData("1.0.0-rc", "1.0.0-12")]
    [InlineData("1.0.0-rc.1", "1.0.0-Beta.1")]
    [InlineData("1.0.0-RC.1", "1.0.0-beta.1")]
    [InlineData("1.0.0-ci.99999999999999999999", "1.0.0-ci.9")]
    public void PreRelease_HigherPrecedenceSortsFirst(string higher, string lower)
    {
        PackageVersionFilter.SortByVersionDescending([lower, higher]).ShouldBe([higher, lower]);
        PackageVersionFilter.SortByVersionDescending([higher, lower]).ShouldBe([higher, lower]);
    }

    [Fact]
    public void ARevisionOutranksANonRevision_AndAReleaseOutranksItsPreRelease()
    {
        PackageVersionFilter.SortByVersionDescending(["1.0.0-rc.1", "1.0.0", "1.0.0.1-rc.1", "1.0.0.1"]).ShouldBe(["1.0.0.1", "1.0.0.1-rc.1", "1.0.0", "1.0.0-rc.1"]);
    }

    [Fact]
    public void PreReleaseText_IsComparedIgnoringCase()
    {
        FeedVersion.TryParse("1.0.0-RC.1", out var upper);
        FeedVersion.TryParse("1.0.0-rc.1", out var lower);

        upper.CompareTo(lower).ShouldBe(0);
        lower.CompareTo(upper).ShouldBe(0);
    }

    [Theory]
    [InlineData("1.0.0-Beta", "1.0.0-rc")]
    [InlineData("1.0.0-beta", "1.0.0-RC")]
    [InlineData("1.0.0-BETA", "1.0.0-rc")]
    public void BetaIsBelowRc_RegardlessOfCase(string lower, string higher)
    {
        PackageVersionFilter.SortByVersionDescending([lower, higher]).ShouldBe([higher, lower]);

        FeedVersion.TryParse(lower, out var a);
        FeedVersion.TryParse(higher, out var b);

        a.CompareTo(b).ShouldBeLessThan(0);
    }
}
