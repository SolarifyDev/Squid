using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.DeploymentExecution.Filtering;

/// <summary>
/// Unit tests for <see cref="StepRoleMatcher"/> — the SINGLE role match shared
/// by the planner (preview) and the executor's runtime target resolution. Pins
/// the rules that keep preview-matched targets identical to the machines a
/// deployment actually runs on, including the whitespace-roles edge that the two
/// old (duplicated) matchers used to disagree on.
/// </summary>
public class StepRoleMatcherTests
{
    [Fact]
    public void NoRolesProperty_IsUnscoped_MatchesAnyTarget()
    {
        var required = StepRoleMatcher.RequiredRoles(Step(roles: null));

        required.ShouldBeEmpty();
        StepRoleMatcher.Matches(required, new[] { "web" }).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOrWhitespaceRoles_IsUnscoped_MatchesAnyTarget(string roles)
    {
        // The old TargetStepMatcher treated whitespace-only as "match NOTHING";
        // the planner treated it as "match ALL". Unified to ALL (unscoped).
        var required = StepRoleMatcher.RequiredRoles(Step(roles));

        required.ShouldBeEmpty();
        StepRoleMatcher.Matches(required, new[] { "web" }).ShouldBeTrue();
    }

    [Fact]
    public void OverlappingRole_Matches()
    {
        var required = StepRoleMatcher.RequiredRoles(Step("web,db"));

        StepRoleMatcher.Matches(required, new[] { "db", "cache" }).ShouldBeTrue();
    }

    [Fact]
    public void NoOverlap_DoesNotMatch()
    {
        var required = StepRoleMatcher.RequiredRoles(Step("web"));

        StepRoleMatcher.Matches(required, new[] { "db" }).ShouldBeFalse();
    }

    [Fact]
    public void RoleMatch_IsCaseInsensitive()
    {
        var required = StepRoleMatcher.RequiredRoles(Step("Web-Server"));

        StepRoleMatcher.Matches(required, new[] { "WEB-SERVER" }).ShouldBeTrue();
    }

    [Fact]
    public void ScopedStep_TargetWithNoRoles_DoesNotMatch()
        => StepRoleMatcher.Matches(new[] { "web" }, System.Array.Empty<string>()).ShouldBeFalse();

    [Fact]
    public void UnscopedStep_TargetWithNoRoles_StillMatches()
        => StepRoleMatcher.Matches(System.Array.Empty<string>(), System.Array.Empty<string>()).ShouldBeTrue();

    private static DeploymentStepDto Step(string roles) => new()
    {
        Id = 1,
        Name = "step",
        Properties = roles == null
            ? new List<DeploymentStepPropertyDto>()
            : new List<DeploymentStepPropertyDto>
            {
                new() { StepId = 1, PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = roles }
            }
    };
}
