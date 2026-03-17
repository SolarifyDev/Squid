using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Authorization;
using Squid.Message.Models.Authorization;

namespace Squid.UnitTests.Services.Authorization;

public class UserRoleServiceTests
{
    private readonly Mock<IUserRoleDataProvider> _userRoleDataProvider = new();
    private readonly Mock<IScopedUserRoleDataProvider> _scopedUserRoleDataProvider = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly UserRoleService _sut;

    public UserRoleServiceTests()
    {
        _sut = new UserRoleService(_userRoleDataProvider.Object, _scopedUserRoleDataProvider.Object, _teamDataProvider.Object);
    }

    [Fact]
    public async Task Create_CallsDataProvider()
    {
        var command = new CreateUserRoleCommand { Name = "TestRole", Description = "A test role", Permissions = new List<string> { "ProjectView" } };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView" });

        await _sut.CreateAsync(command);

        _userRoleDataProvider.Verify(x => x.AddAsync(It.Is<UserRole>(r => r.Name == "TestRole" && !r.IsBuiltIn), true, It.IsAny<CancellationToken>()), Times.Once);
        _userRoleDataProvider.Verify(x => x.SetPermissionsAsync(It.IsAny<int>(), It.Is<List<string>>(p => p.Contains("ProjectView")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_InvalidPermissionName_Throws()
    {
        var command = new CreateUserRoleCommand { Name = "BadRole", Description = "Desc", Permissions = new List<string> { "NotARealPermission" } };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.CreateAsync(command));

        ex.Message.ShouldContain("NotARealPermission");
    }

    [Fact]
    public async Task Update_Success_UpdatesRoleAndPermissions()
    {
        var command = new UpdateUserRoleCommand { Id = 1, Name = "Updated", Description = "New desc", Permissions = new List<string> { "ProjectView", "ProjectEdit" } };

        _userRoleDataProvider.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 1, Name = "Old", IsBuiltIn = false });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView", "ProjectEdit" });

        var result = await _sut.UpdateAsync(command);

        result.Name.ShouldBe("Updated");
        result.Permissions.Count.ShouldBe(2);
        _userRoleDataProvider.Verify(x => x.UpdateAsync(It.Is<UserRole>(r => r.Name == "Updated"), true, It.IsAny<CancellationToken>()), Times.Once);
        _userRoleDataProvider.Verify(x => x.SetPermissionsAsync(1, It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        var command = new UpdateUserRoleCommand { Id = 999, Name = "Updated", Description = "Desc", Permissions = new List<string> { "ProjectView" } };

        _userRoleDataProvider.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((UserRole)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.UpdateAsync(command));

        ex.Message.ShouldContain("999");
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task Update_BuiltInRole_Throws()
    {
        var command = new UpdateUserRoleCommand { Id = 1, Name = "Updated", Description = "Desc", Permissions = new List<string> { "ProjectView" } };

        _userRoleDataProvider.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 1, Name = "System Administrator", IsBuiltIn = true });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.UpdateAsync(command));

        ex.Message.ShouldContain("built-in");
    }

    [Fact]
    public async Task Delete_NotFound_Throws()
    {
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((UserRole)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(999));

        ex.Message.ShouldContain("999");
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task Delete_BuiltInRole_Throws()
    {
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 1, Name = "System Administrator", IsBuiltIn = true });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(1));

        ex.Message.ShouldContain("built-in");
    }

    [Fact]
    public async Task Delete_Success_CallsDeleteAsync()
    {
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 1, Name = "Custom", IsBuiltIn = false });

        await _sut.DeleteAsync(1);

        _userRoleDataProvider.Verify(x => x.DeleteAsync(It.Is<UserRole>(r => r.Id == 1), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAll_ReturnsMappedDtos()
    {
        _userRoleDataProvider.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<UserRole>
        {
            new() { Id = 1, Name = "Role A", Description = "Desc A", IsBuiltIn = true },
            new() { Id = 2, Name = "Role B", Description = "Desc B", IsBuiltIn = false },
        });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView" });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "DeploymentCreate" });

        var result = await _sut.GetAllAsync();

        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("Role A");
        result[0].IsBuiltIn.ShouldBeTrue();
        result[0].Permissions.ShouldContain("ProjectView");
        result[1].Name.ShouldBe("Role B");
        result[1].Permissions.ShouldContain("DeploymentCreate");
    }

    [Fact]
    public async Task GetById_Found_ReturnsMappedDto()
    {
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 1, Name = "Dev", Description = "Developer", IsBuiltIn = false });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView", "ProjectEdit" });

        var result = await _sut.GetByIdAsync(1);

        result.Id.ShouldBe(1);
        result.Name.ShouldBe("Dev");
        result.Description.ShouldBe("Developer");
        result.Permissions.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetById_NotFound_Throws()
    {
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((UserRole)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.GetByIdAsync(999));

        ex.Message.ShouldContain("999");
    }

    [Fact]
    public async Task AssignRoleToTeam_CreatesScoped()
    {
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 50, SpaceId = 1 };

        _userRoleDataProvider.Setup(x => x.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 50, Name = "Dev" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.AssignRoleToTeamAsync(command);

        result.TeamId.ShouldBe(10);
        result.UserRoleId.ShouldBe(50);
        result.SpaceId.ShouldBe(1);
        _scopedUserRoleDataProvider.Verify(x => x.AddAsync(It.Is<ScopedUserRole>(r => r.TeamId == 10 && r.UserRoleId == 50 && r.SpaceId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveRoleFromTeam_CallsDelete()
    {
        await _sut.RemoveRoleFromTeamAsync(100);

        _scopedUserRoleDataProvider.Verify(x => x.DeleteAsync(It.Is<ScopedUserRole>(r => r.Id == 100), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTeamRoles_ReturnsMappedDtos()
    {
        _scopedUserRoleDataProvider.Setup(x => x.GetByTeamIdsAsync(It.Is<List<int>>(l => l.Contains(10)), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 1 },
        });
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 50, Name = "Dev" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 5 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.GetTeamRolesAsync(10);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(100);
        result[0].UserRoleName.ShouldBe("Dev");
        result[0].ProjectIds.ShouldContain(5);
    }

    [Fact]
    public async Task GetAllPermissions_ReturnsAllEnumValues()
    {
        var result = await _sut.GetAllPermissionsAsync();

        result.Count.ShouldBeGreaterThan(0);

        var names = result.Select(p => p.Name).ToList();
        names.ShouldContain("ProjectView");
        names.ShouldContain("AdministerSystem");

        result.All(p => !string.IsNullOrEmpty(p.Scope)).ShouldBeTrue();
    }

    [Fact]
    public async Task GetUserPermissions_AggregatesFromAllTeamRoles()
    {
        _teamDataProvider.Setup(x => x.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 10, 20 });
        _scopedUserRoleDataProvider.Setup(x => x.GetByTeamIdsAsync(It.Is<List<int>>(l => l.Contains(10) && l.Contains(20)), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50 },
            new() { Id = 101, TeamId = 20, UserRoleId = 51 },
        });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView" });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(51, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView", "DeploymentCreate" });

        var result = await _sut.GetUserPermissionsAsync(42);

        result.UserId.ShouldBe(42);
        result.Permissions.Count.ShouldBe(2);
        result.Permissions.ShouldContain("ProjectView");
        result.Permissions.ShouldContain("DeploymentCreate");
    }

    [Fact]
    public async Task UpdateRoleScope_Success_UpdatesAllScopes()
    {
        var command = new UpdateRoleScopeCommand
        {
            TeamId = 10,
            ScopedUserRoleId = 100,
            ProjectIds = new List<int> { 1, 2 },
            EnvironmentIds = new List<int> { 3 },
            ProjectGroupIds = new List<int>(),
        };

        _scopedUserRoleDataProvider.Setup(x => x.GetByTeamIdsAsync(It.Is<List<int>>(l => l.Contains(10)), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 1 },
        });
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 50, Name = "Dev" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 1, 2 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 3 });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.UpdateRoleScopeAsync(command);

        result.Id.ShouldBe(100);
        _scopedUserRoleDataProvider.Verify(x => x.SetProjectScopeAsync(100, It.Is<List<int>>(l => l.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(x => x.SetEnvironmentScopeAsync(100, It.Is<List<int>>(l => l.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(x => x.SetProjectGroupScopeAsync(100, It.Is<List<int>>(l => l.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateRoleScope_NotFound_Throws()
    {
        var command = new UpdateRoleScopeCommand
        {
            TeamId = 10,
            ScopedUserRoleId = 999,
            ProjectIds = new List<int>(),
            EnvironmentIds = new List<int>(),
            ProjectGroupIds = new List<int>(),
        };

        _scopedUserRoleDataProvider.Setup(x => x.GetByTeamIdsAsync(It.Is<List<int>>(l => l.Contains(10)), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ScopedUserRole>());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.UpdateRoleScopeAsync(command));

        ex.Message.ShouldContain("999");
    }
}
