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
}
