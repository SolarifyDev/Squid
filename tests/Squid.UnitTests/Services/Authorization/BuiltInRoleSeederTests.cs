using System;
using System.Linq;
using Squid.Core.Services.DataSeeding;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Authorization;

public class BuiltInRoleSeederTests
{
    [Fact]
    public void AllBuiltInRoles_HaveAtLeastOnePermission()
    {
        foreach (var role in BuiltInRoles.All)
        {
            role.Permissions.ShouldNotBeEmpty($"Role '{role.Name}' has no permissions");
        }
    }

    [Fact]
    public void SpaceOwner_ContainsAllSpaceLevelPermissions()
    {
        var allSpacePermissions = Enum.GetValues<Permission>().Where(p => p.CanApplyAtSpaceLevel()).ToList();

        foreach (var permission in allSpacePermissions)
        {
            BuiltInRoles.SpaceOwner.Permissions.ShouldContain(permission, $"SpaceOwner is missing {permission}");
        }
    }

    [Fact]
    public void SystemAdmin_ContainsAdministerSystem()
    {
        BuiltInRoles.SystemAdministrator.Permissions.ShouldContain(Permission.AdministerSystem);
    }

    [Fact]
    public void BuiltInRoles_NoDuplicatePermissions()
    {
        foreach (var role in BuiltInRoles.All)
        {
            var distinct = role.Permissions.Distinct().ToList();

            distinct.Count.ShouldBe(role.Permissions.Count, $"Role '{role.Name}' has duplicate permissions");
        }
    }

    [Fact]
    public void AllBuiltInRoles_HaveNameAndDescription()
    {
        foreach (var role in BuiltInRoles.All)
        {
            role.Name.ShouldNotBeNullOrWhiteSpace($"A built-in role has no name");
            role.Description.ShouldNotBeNullOrWhiteSpace($"Role '{role.Name}' has no description");
        }
    }

    // ─── SystemServiceAccount ──────────────────────────────────────────────
    // Reserved role used by the InternalUser bootstrap key (Tentacle install
    // scripts). Pinned narrowly: MachineView + MachineCreate + MachineEdit
    // only. No MachineDelete, no account / env / task. Any drift here directly
    // affects bootstrap-key blast radius -- it MUST stay least-privilege.

    [Fact]
    public void SystemServiceAccount_HasExactlyThreeMachinePermissions()
    {
        BuiltInRoles.SystemServiceAccount.Permissions.Count.ShouldBe(3,
            customMessage: "SystemServiceAccount is the bootstrap-key role -- it must stay narrow. " +
                          "Any expansion increases blast radius if the shared bootstrap key leaks.");

        BuiltInRoles.SystemServiceAccount.Permissions.ShouldContain(Permission.MachineView);
        BuiltInRoles.SystemServiceAccount.Permissions.ShouldContain(Permission.MachineCreate);
        BuiltInRoles.SystemServiceAccount.Permissions.ShouldContain(Permission.MachineEdit);
    }

    [Fact]
    public void SystemServiceAccount_DoesNotGrantMachineDelete()
    {
        BuiltInRoles.SystemServiceAccount.Permissions.ShouldNotContain(Permission.MachineDelete,
            customMessage: "Bootstrap key MUST NOT be able to delete machines. " +
                          "Octopus's registration-token equivalent also withholds Delete.");
    }

    [Fact]
    public void SystemServiceAccount_DoesNotGrantDeploymentOrAccountOrEnvironmentPermissions()
    {
        var forbidden = new[]
        {
            Permission.DeploymentCreate, Permission.DeploymentView,
            Permission.AccountCreate, Permission.AccountView,
            Permission.EnvironmentCreate, Permission.EnvironmentView,
            Permission.TaskCreate, Permission.TaskCancel,
        };

        foreach (var permission in forbidden)
        {
            BuiltInRoles.SystemServiceAccount.Permissions.ShouldNotContain(permission,
                customMessage: $"SystemServiceAccount must NOT grant {permission}. " +
                              $"Bootstrap key is single-purpose (machine register) -- adding permissions " +
                              $"expands what a leaked key can do.");
        }
    }

    [Fact]
    public void SystemServiceAccount_NameAndDescriptionFlagItAsReserved()
    {
        // Operator-facing warning: don't assign to humans. Pinned so a future
        // rename doesn't accidentally drop the warning from the description.
        BuiltInRoles.SystemServiceAccount.Name.ShouldBe("System Service Account");
        BuiltInRoles.SystemServiceAccount.Description.ShouldContain("Do NOT assign to human users",
            customMessage: "Description must explicitly warn against assigning to humans.");
    }
}
