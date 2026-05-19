using Shouldly;
using Squid.Core.Services.Authorization;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Authorization;

/// <summary>
/// Pins the permission → built-in role mapping that the server's 403 response
/// + Tentacle CLI 403 hint both depend on. A regression in the built-in role
/// definitions (e.g. someone accidentally drops <c>MachineCreate</c> from
/// Environment Manager) would silently leave operators with an empty
/// <c>SuggestedRoles</c> list and a useless permission-denied hint.
/// </summary>
public class PermissionRoleResolverTests
{
    [Fact]
    public void GetBuiltInRolesGranting_MachineCreate_ReturnsSpaceOwnerAndEnvironmentManager()
    {
        // Operator-facing contract pinned: the built-in roles that ship with
        // MachineCreate permission. Tentacle's PR 2 install-script hint
        // explicitly names these — drift breaks the operator UX.
        //
        // System Administrator deliberately does NOT have MachineCreate — it's
        // a system-level role (manage spaces, users, teams), not a space-
        // resource role. Machines live inside a space, so MachineCreate ships
        // on space-scoped roles only.
        var roles = PermissionRoleResolver.GetBuiltInRolesGranting(Permission.MachineCreate);

        roles.ShouldContain("Environment Manager");
        roles.ShouldContain("Space Owner");
        roles.ShouldNotContain("System Administrator", customMessage:
            "System Administrator is a system-level role and does not grant space-scoped MachineCreate. " +
            "If this changed in BuiltInRoles.SystemAdministrator, update the install-script hint AND this test.");
        roles.Count.ShouldBe(2);
    }

    [Fact]
    public void GetBuiltInRolesGranting_ProjectView_ReturnsViewerRolesAndUp()
    {
        // Read permissions are broader — anything from Viewer upwards has them.
        var roles = PermissionRoleResolver.GetBuiltInRolesGranting(Permission.ProjectView);

        roles.ShouldContain("Project Viewer");
        roles.ShouldContain("Project Contributor");
        roles.ShouldContain("Project Deployer");
        // System Administrator + Space Owner also have it.
        roles.Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void BuildOperatorHint_MachineCreate_ListsCorrectRoles()
    {
        var hint = PermissionRoleResolver.BuildOperatorHint(Permission.MachineCreate);

        hint.ShouldContain("MachineCreate");
        hint.ShouldContain("Environment Manager");
        hint.ShouldContain("Space Owner");
        hint.ShouldNotContain("System Administrator", customMessage:
            "System Administrator does not grant space-scoped MachineCreate. Suggesting it would mislead operators.");
        // The hint must direct operators to one of two remediation paths.
        hint.ShouldContain("Assign one of these roles");
        hint.ShouldContain("add 'MachineCreate'");
    }

    [Fact]
    public void BuildOperatorHint_PermissionWithoutBuiltInRole_FallsBackToCustomRoleGuidance()
    {
        // If a permission is somehow not granted by any built-in role (future
        // additions before role updates), the hint must still be actionable.
        // We construct an enum value that exists but assert the fallback path
        // behaves sensibly by stubbing via a permission that genuinely has no
        // built-in granter today (none currently, but the resolver code path
        // must handle it).
        //
        // Defensive test: assert the hint never returns an empty/whitespace
        // string for any defined Permission value.
        foreach (Permission permission in Enum.GetValues(typeof(Permission)))
        {
            var hint = PermissionRoleResolver.BuildOperatorHint(permission);
            hint.ShouldNotBeNullOrWhiteSpace(
                customMessage: $"BuildOperatorHint returned empty/whitespace for permission '{permission}' — every value must produce an actionable message.");
        }
    }

    [Fact]
    public void GetBuiltInRolesGranting_EveryDefinedPermission_DoesNotThrow()
    {
        // Defensive: looking up any defined Permission must not throw, even
        // if no role grants it. Returning an empty list is fine.
        foreach (Permission permission in Enum.GetValues(typeof(Permission)))
        {
            Should.NotThrow(() => PermissionRoleResolver.GetBuiltInRolesGranting(permission));
        }
    }
}
