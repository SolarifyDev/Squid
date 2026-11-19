using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>The restriction-aware permission rule (D11) on the 2026.1 <c>users/{id}/permissions</c> shape.</summary>
public sealed class OctopusRestPermissionGateTests
{
    private const string Space = "Spaces-1";

    [Fact]
    public void RequiredPermissions_Pinned()
    {
        // Narrowing this list lets a project- or environment-limited key import a silently partial space.
        OctopusRestPermissionGate.RequiredPermissions.ShouldBe(["ProjectView", "ProcessView", "VariableView", "LibraryVariableSetView", "EnvironmentView", "FeedView", "AccountView", "TeamView", "CertificateView", "ReleaseView", "DeploymentView", "TaskView"]);
    }

    [Theory]
    [InlineData("users-permissions-unrestricted", true, new string[0])]
    [InlineData("users-permissions-restricted", false, new[] { "VariableView" })]
    public void Fixture_EvaluatedAsTheRealServerWouldGrantIt(string fixture, bool sufficient, string[] restricted)
    {
        var result = OctopusRestPermissionGate.Evaluate(Fixture(fixture), Space);

        result.Sufficient.ShouldBe(sufficient);
        result.Restricted.ShouldBe(restricted);
        result.Missing.ShouldBeEmpty();
        result.Incomplete.ShouldBeFalse();
        result.OctopusSpaceId.ShouldBe(Space);
    }

    [Theory]
    [InlineData("projects-all", "environments-all", "tenants-all", "projectgroups-all", false)]
    [InlineData("projects-unrelated", "environments-unrelated", "tenants-unrelated", "projectgroups-unrelated", false)]
    [InlineData("PROJECTS-ALL", "Environments-Unrelated", "tenants-all", "projectgroups-unrelated", false)]
    [InlineData("Projects-1", "environments-all", "tenants-all", "projectgroups-unrelated", true)]
    [InlineData("projects-all", "Environments-2", "tenants-all", "projectgroups-unrelated", true)]
    [InlineData("projects-all", "environments-all", "Tenants-1", "projectgroups-unrelated", true)]
    [InlineData("projects-all", "environments-all", "tenants-all", "ProjectGroups-1", true)]
    [InlineData("environments-all", "environments-all", "tenants-all", "projectgroups-unrelated", true)]
    [InlineData(null, null, null, null, false)]
    public void Grant_RestrictedByAnyExplicitIdInAnyDimension(string project, string environment, string tenant, string projectGroup, bool restricted)
    {
        var grant = new OctopusRestPermissionGrantDto { SpaceId = Space, RestrictedToProjectIds = List(project), RestrictedToEnvironmentIds = List(environment), RestrictedToTenantIds = List(tenant), RestrictedToProjectGroupIds = List(projectGroup) };

        OctopusRestPermissionGate.IsUnrestricted(grant).ShouldBe(!restricted);
        OctopusRestPermissionGate.Evaluate(AllGranted(("VariableView", grant)), Space).Restricted.ShouldBe(restricted ? ["VariableView"] : []);
    }

    [Fact]
    public void Grant_SentinelBesideAnExplicitId_StillRestricted()
    {
        var grant = Unrestricted(Space);
        grant.RestrictedToEnvironmentIds = ["environments-all", "Environments-3"];

        OctopusRestPermissionGate.IsUnrestricted(grant).ShouldBeFalse();
    }

    [Fact]
    public void Grants_AreOred_OneUnrestrictedGrantIsEnough()
    {
        var limited = Unrestricted(Space);
        limited.RestrictedToProjectIds = ["Projects-1"];

        var result = OctopusRestPermissionGate.Evaluate(AllGranted(("VariableView", limited), ("VariableView", Unrestricted(Space))), Space);

        result.Sufficient.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Spaces-2", Space, true)]
    [InlineData("spaces-1", Space, false)]
    [InlineData("Spaces-2", null, false)]
    [InlineData(null, null, false)]
    public void Grant_CountsOnlyInItsSpace_ExceptOnAServerWithoutSpaces(string grantSpace, string requestedSpace, bool missing)
    {
        var result = OctopusRestPermissionGate.Evaluate(AllGranted(("AccountView", Unrestricted(grantSpace))), requestedSpace);

        result.Missing.ShouldBe(missing ? ["AccountView"] : []);
        result.Sufficient.ShouldBe(!missing);
    }

    [Fact]
    public void PermissionAbsent_Missing_EvenWhenANarrowerOneIsGranted()
    {
        var permissions = AllGranted();
        permissions.SpacePermissions.Remove("VariableView");
        permissions.SpacePermissions["VariableViewUnscoped"] = [Unrestricted(Space)];

        var result = OctopusRestPermissionGate.Evaluate(permissions, Space);

        result.Missing.ShouldBe(["VariableView"]);
        result.Restricted.ShouldBeEmpty();
        result.Sufficient.ShouldBeFalse();
    }

    [Fact]
    public void PermissionNames_MatchedIgnoringCase()
    {
        var permissions = new OctopusRestUserPermissionsDto { SpacePermissions = OctopusRestPermissionGate.RequiredPermissions.ToDictionary(p => p.ToUpperInvariant(), _ => new List<OctopusRestPermissionGrantDto> { Unrestricted(Space) }) };

        OctopusRestPermissionGate.Evaluate(permissions, Space).Sufficient.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public void IncompletePermissionSet_NeverSufficient(bool? complete, bool incomplete)
    {
        var permissions = AllGranted();
        permissions.IsPermissionsComplete = complete;

        var result = OctopusRestPermissionGate.Evaluate(permissions, Space);

        result.Incomplete.ShouldBe(incomplete);
        result.Sufficient.ShouldBe(!incomplete);
    }

    [Fact]
    public void NoPermissionSetAtAll_EverythingMissing()
    {
        var result = OctopusRestPermissionGate.Evaluate(null, Space);

        result.Missing.ShouldBe(OctopusRestPermissionGate.RequiredPermissions);
        result.Sufficient.ShouldBeFalse();
    }

    [Fact]
    public void Unreadable_NothingProven()
    {
        var result = OctopusRestPermissionGate.Unreadable(Space);

        result.Sufficient.ShouldBeFalse();
        result.Incomplete.ShouldBeTrue();
        result.Missing.ShouldBe(OctopusRestPermissionGate.RequiredPermissions);
    }

    [Fact]
    public void EnsureSufficient_RefusesWithEveryRestrictedAndMissingPermissionNamed()
    {
        var permissions = new OctopusServerPermissionsDto { OctopusSpaceId = Space, Restricted = ["VariableView", "ProjectView"], Missing = ["AccountView"], Incomplete = true };

        var ex = Should.Throw<OctopusImportApiKeyRestrictedException>(() => OctopusRestPermissionGate.EnsureSufficient(permissions));

        ex.Permissions.ShouldBeSameAs(permissions);
        ex.Message.ShouldContain("Spaces-1");
        ex.Message.ShouldContain("VariableView, ProjectView");
        ex.Message.ShouldContain("Not granted: AccountView");
        ex.Message.ShouldContain("could not report");
        ex.Message.ShouldContain("read-only service account");
    }

    [Fact]
    public void EnsureSufficient_SufficientKey_Passes()
    {
        Should.NotThrow(() => OctopusRestPermissionGate.EnsureSufficient(OctopusRestPermissionGate.Evaluate(AllGranted(), Space)));
    }

    private static OctopusRestUserPermissionsDto Fixture(string name) => JsonSerializer.Deserialize<OctopusRestUserPermissionsDto>(OctopusServer2026Fixtures.Read(name));

    // Every required permission granted without restriction in Spaces-1, then the given grants in place of the defaults.
    private static OctopusRestUserPermissionsDto AllGranted(params (string Permission, OctopusRestPermissionGrantDto Grant)[] overrides)
    {
        var permissions = OctopusRestPermissionGate.RequiredPermissions.ToDictionary(p => p, _ => new List<OctopusRestPermissionGrantDto> { Unrestricted(Space) });

        foreach (var group in overrides.GroupBy(o => o.Permission)) permissions[group.Key] = group.Select(o => o.Grant).ToList();

        return new OctopusRestUserPermissionsDto { SpacePermissions = permissions, IsPermissionsComplete = true };
    }

    private static OctopusRestPermissionGrantDto Unrestricted(string space) => new() { SpaceId = space, RestrictedToProjectIds = ["projects-all"], RestrictedToEnvironmentIds = ["environments-all"], RestrictedToTenantIds = ["tenants-all"], RestrictedToProjectGroupIds = ["projectgroups-unrelated"] };

    private static List<string> List(string value) => value == null ? null : [value];
}
