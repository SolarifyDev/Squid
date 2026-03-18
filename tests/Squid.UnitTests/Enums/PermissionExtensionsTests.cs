using System;
using System.Linq;
using Squid.Message.Enums;

namespace Squid.UnitTests.Enums;

public class PermissionExtensionsTests
{
    [Fact]
    public void AllPermissions_HavePermissionScopeAttribute()
    {
        var allPermissions = Enum.GetValues<Permission>();

        foreach (var permission in allPermissions)
        {
            Should.NotThrow(() => permission.GetScope(), $"Permission {permission} is missing [PermissionScope] attribute.");
        }
    }

    [Theory]
    [InlineData(Permission.ProjectView, PermissionScope.SpaceOnly)]
    [InlineData(Permission.AdministerSystem, PermissionScope.SystemOnly)]
    [InlineData(Permission.TaskView, PermissionScope.Mixed)]
    public void GetScope_ReturnsCorrectScope(Permission permission, PermissionScope expected)
    {
        permission.GetScope().ShouldBe(expected);
    }

    [Theory]
    [InlineData(Permission.ProjectView, true)]
    [InlineData(Permission.AdministerSystem, false)]
    [InlineData(Permission.TaskView, true)]
    public void CanApplyAtSpaceLevel_ReturnsCorrectly(Permission permission, bool expected)
    {
        permission.CanApplyAtSpaceLevel().ShouldBe(expected);
    }

    [Theory]
    [InlineData(Permission.ProjectView, false)]
    [InlineData(Permission.AdministerSystem, true)]
    [InlineData(Permission.TaskView, true)]
    public void CanApplyAtSystemLevel_ReturnsCorrectly(Permission permission, bool expected)
    {
        permission.CanApplyAtSystemLevel().ShouldBe(expected);
    }

    [Fact]
    public void GetScope_UndefinedPermission_ThrowsInvalidOperation()
    {
        var bogus = (Permission)9999;

        Should.Throw<InvalidOperationException>(() => bogus.GetScope())
            .Message.ShouldContain("missing [PermissionScope] attribute");
    }

    [Fact]
    public void SpaceOnlyPermissions_Count()
    {
        var spaceOnly = Enum.GetValues<Permission>().Count(p => p.GetScope() == PermissionScope.SpaceOnly);

        spaceOnly.ShouldBeGreaterThan(30);
    }

    [Fact]
    public void SystemOnlyPermissions_Count()
    {
        var systemOnly = Enum.GetValues<Permission>().Count(p => p.GetScope() == PermissionScope.SystemOnly);

        systemOnly.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void MixedPermissions_Count()
    {
        var mixed = Enum.GetValues<Permission>().Count(p => p.GetScope() == PermissionScope.Mixed);

        mixed.ShouldBeGreaterThanOrEqualTo(6);
    }
}
