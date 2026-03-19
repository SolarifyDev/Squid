using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;

namespace Squid.UnitTests.Services.Deployments.Channels;

public class ChannelVersionRuleValidatorTests
{
    private readonly ChannelVersionRuleValidator _validator = new();

    // === Version Range Parsing ===

    [Theory]
    [InlineData("1.0.0", true, 1, 0, 0, null)]
    [InlineData("2.3.4", true, 2, 3, 4, null)]
    [InlineData("1.0", true, 1, 0, 0, null)]
    [InlineData("1.0.0-beta", true, 1, 0, 0, "beta")]
    [InlineData("3.2.1-rc.1", true, 3, 2, 1, "rc.1")]
    [InlineData("", false, 0, 0, 0, null)]
    [InlineData("abc", false, 0, 0, 0, null)]
    [InlineData("1", false, 0, 0, 0, null)]
    public void TryParseVersion(string input, bool expectedSuccess, int major, int minor, int patch, string preRelease)
    {
        var success = ChannelVersionRuleValidator.TryParseVersion(input, out var result);

        success.ShouldBe(expectedSuccess);

        if (expectedSuccess)
        {
            result.Major.ShouldBe(major);
            result.Minor.ShouldBe(minor);
            result.Patch.ShouldBe(patch);
            result.PreRelease.ShouldBe(preRelease);
        }
    }

    // === Version Range Satisfaction ===

    [Theory]
    [InlineData("1.5.0", "[1.0,2.0)", true)]       // inclusive min, exclusive max
    [InlineData("1.0.0", "[1.0,2.0)", true)]       // exact min boundary (inclusive)
    [InlineData("2.0.0", "[1.0,2.0)", false)]      // exact max boundary (exclusive)
    [InlineData("0.9.0", "[1.0,2.0)", false)]      // below min
    [InlineData("3.0.0", "(,3.0]", true)]          // unbounded min, inclusive max
    [InlineData("3.0.1", "(,3.0]", false)]         // above inclusive max
    [InlineData("1.0.0", "(1.0,)", false)]         // exclusive min, unbounded max → exactly 1.0 fails
    [InlineData("1.0.1", "(1.0,)", true)]          // above exclusive min
    [InlineData("2.0.0", "[2.0.0]", true)]         // exact match bracket
    [InlineData("2.0.1", "[2.0.0]", false)]        // not exact match
    [InlineData("1.0.0", "1.0.0", true)]           // bare version = exact
    [InlineData("1.0.1", "1.0.0", false)]          // bare version mismatch
    public void SatisfiesVersionRange(string version, string range, bool expected)
    {
        ChannelVersionRuleValidator.TryParseVersion(version, out var semVer);

        var result = ChannelVersionRuleValidator.SatisfiesVersionRange(semVer, range);

        result.ShouldBe(expected);
    }

    // === Pre-Release Tag ===

    [Theory]
    [InlineData("beta", "^beta", true)]
    [InlineData("beta.1", "^beta", true)]
    [InlineData("alpha", "^beta", false)]
    [InlineData("rc.1", "^rc", true)]
    [InlineData("", "^beta", false)]               // empty pre-release fails
    [InlineData("beta", "^(alpha|beta)$", true)]
    [InlineData("gamma", "^(alpha|beta)$", false)]
    public void SatisfiesPreReleaseTag(string preRelease, string pattern, bool expected)
    {
        var result = ChannelVersionRuleValidator.SatisfiesPreReleaseTag(preRelease, pattern);

        result.ShouldBe(expected);
    }

    [Fact]
    public void SatisfiesPreReleaseTag_InvalidRegex_ReturnsTrue()
    {
        var result = ChannelVersionRuleValidator.SatisfiesPreReleaseTag("beta", "[invalid");

        result.ShouldBeTrue();
    }

    // === Full Rule Validation ===

    [Fact]
    public void Validate_NoRules_ReturnsNoViolations()
    {
        var packages = new[] { new SelectedPackageInfo("Deploy", "1.0.0") };

        var violations = _validator.Validate([], packages);

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RuleWithVersionRange_PackageSatisfies_NoViolation()
    {
        var rules = new[] { MakeRule(versionRange: "[1.0,2.0)") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "1.5.0") };

        var violations = _validator.Validate(rules, packages);

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RuleWithVersionRange_PackageViolates_ReturnsViolation()
    {
        var rules = new[] { MakeRule(versionRange: "[1.0,2.0)") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "3.0.0") };

        var violations = _validator.Validate(rules, packages);

        violations.Count.ShouldBe(1);
        violations[0].ActionName.ShouldBe("Deploy");
        violations[0].Version.ShouldBe("3.0.0");
    }

    [Fact]
    public void Validate_RuleWithPreReleaseTag_NoPreRelease_ReturnsViolation()
    {
        var rules = new[] { MakeRule(preReleaseTag: "^beta") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "1.0.0") };

        var violations = _validator.Validate(rules, packages);

        violations.Count.ShouldBe(1);
    }

    [Fact]
    public void Validate_RuleWithPreReleaseTag_MatchingTag_NoViolation()
    {
        var rules = new[] { MakeRule(preReleaseTag: "^beta") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "1.0.0-beta.1") };

        var violations = _validator.Validate(rules, packages);

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RuleScopedToAction_DifferentAction_NotApplied()
    {
        var rules = new[] { MakeRule(actionNames: "BuildImage", versionRange: "[1.0,2.0)") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "3.0.0") };

        var violations = _validator.Validate(rules, packages);

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RuleScopedToAction_MatchingAction_Applied()
    {
        var rules = new[] { MakeRule(actionNames: "Deploy", versionRange: "[1.0,2.0)") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "3.0.0") };

        var violations = _validator.Validate(rules, packages);

        violations.Count.ShouldBe(1);
    }

    [Fact]
    public void Validate_RuleScopedToMultipleActions_CsvMatch()
    {
        var rules = new[] { MakeRule(actionNames: "Deploy, BuildImage", versionRange: "[1.0,2.0)") };
        var packages = new[] { new SelectedPackageInfo("BuildImage", "3.0.0") };

        var violations = _validator.Validate(rules, packages);

        violations.Count.ShouldBe(1);
    }

    [Fact]
    public void Validate_GlobalRule_AppliesAllActions()
    {
        var rules = new[] { MakeRule(actionNames: "", versionRange: "[1.0,2.0)") };
        var packages = new[]
        {
            new SelectedPackageInfo("Deploy", "1.5.0"),
            new SelectedPackageInfo("BuildImage", "3.0.0")
        };

        var violations = _validator.Validate(rules, packages);

        violations.Count.ShouldBe(1);
        violations[0].ActionName.ShouldBe("BuildImage");
    }

    [Fact]
    public void Validate_MultipleRules_BothChecked()
    {
        var rules = new[]
        {
            MakeRule(versionRange: "[1.0,2.0)"),
            MakeRule(preReleaseTag: "^beta")
        };
        var packages = new[] { new SelectedPackageInfo("Deploy", "1.5.0") };

        var violations = _validator.Validate(rules, packages);

        // Satisfies version range but fails pre-release tag (no pre-release on "1.5.0")
        violations.Count.ShouldBe(1);
    }

    [Fact]
    public void Validate_CombinedRule_BothConditionsMet_NoViolation()
    {
        var rules = new[] { MakeRule(versionRange: "[1.0,2.0)", preReleaseTag: "^beta") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "1.5.0-beta.2") };

        var violations = _validator.Validate(rules, packages);

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_UnparseableVersion_SkipsValidation()
    {
        var rules = new[] { MakeRule(versionRange: "[1.0,2.0)") };
        var packages = new[] { new SelectedPackageInfo("Deploy", "latest") };

        var violations = _validator.Validate(rules, packages);

        violations.ShouldBeEmpty();
    }

    private static ChannelVersionRule MakeRule(string actionNames = "", string versionRange = "", string preReleaseTag = "")
    {
        return new ChannelVersionRule
        {
            Id = 1,
            ChannelId = 1,
            ActionNames = actionNames,
            VersionRange = versionRange,
            PreReleaseTag = preReleaseTag,
            SortOrder = 0
        };
    }
}
