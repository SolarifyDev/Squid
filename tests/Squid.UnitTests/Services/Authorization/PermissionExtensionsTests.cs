using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Authorization;

public class PermissionExtensionsTests
{
    [Theory]
    [InlineData(Permission.ProjectView, PermissionScope.SpaceOnly)]
    [InlineData(Permission.TaskView, PermissionScope.Mixed)]
    [InlineData(Permission.AdministerSystem, PermissionScope.SystemOnly)]
    [InlineData(Permission.TeamView, PermissionScope.Mixed)]
    [InlineData(Permission.UserView, PermissionScope.SystemOnly)]
    public void GetScope_ReturnsCorrectScope(Permission permission, PermissionScope expected)
    {
        permission.GetScope().ShouldBe(expected);
    }

    [Theory]
    [InlineData(Permission.ProjectView, true)]
    [InlineData(Permission.TaskView, true)]
    [InlineData(Permission.AdministerSystem, false)]
    [InlineData(Permission.UserView, false)]
    public void CanApplyAtSpaceLevel_ReturnsCorrectResult(Permission permission, bool expected)
    {
        permission.CanApplyAtSpaceLevel().ShouldBe(expected);
    }

    [Theory]
    [InlineData(Permission.ProjectView, false)]
    [InlineData(Permission.TaskView, true)]
    [InlineData(Permission.AdministerSystem, true)]
    [InlineData(Permission.UserView, true)]
    public void CanApplyAtSystemLevel_ReturnsCorrectResult(Permission permission, bool expected)
    {
        permission.CanApplyAtSystemLevel().ShouldBe(expected);
    }
}
