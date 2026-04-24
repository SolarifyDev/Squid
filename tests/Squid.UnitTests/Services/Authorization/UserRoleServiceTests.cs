using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Authorization;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;

namespace Squid.UnitTests.Services.Authorization;

public class UserRoleServiceTests
{
    private readonly Mock<IUserRoleDataProvider> _userRoleDataProvider = new();
    private readonly Mock<IScopedUserRoleDataProvider> _scopedUserRoleDataProvider = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly Mock<IAuthorizationService> _authorizationService = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly UserRoleService _sut;

    public UserRoleServiceTests()
    {
        // Default: caller is user 42 and does NOT hold AdministerSystem. Individual tests
        // override as needed. This is the safer default — a forgotten override shouldn't
        // accidentally bypass the P0-D.2 anti-escalation guard.
        _currentUser.Setup(u => u.Id).Returns(42);
        _authorizationService
            .Setup(a => a.CheckPermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionCheckResult.Denied("default deny in unit test"));

        _sut = new UserRoleService(
            _userRoleDataProvider.Object,
            _scopedUserRoleDataProvider.Object,
            _teamDataProvider.Object,
            _authorizationService.Object,
            _currentUser.Object);
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
    public async Task GetAll_SpaceOnlyRole_ScopeFlags()
    {
        _userRoleDataProvider.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<UserRole>
        {
            new() { Id = 1, Name = "Project Deployer", IsBuiltIn = true },
        });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView", "DeploymentCreate" });

        var result = await _sut.GetAllAsync();

        result[0].CanApplyAtSpaceLevel.ShouldBeTrue();
        result[0].CanApplyAtSystemLevel.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAll_SystemOnlyRole_ScopeFlags()
    {
        _userRoleDataProvider.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<UserRole>
        {
            new() { Id = 1, Name = "System Administrator", IsBuiltIn = true },
        });
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "AdministerSystem", "UserView", "SpaceView" });

        var result = await _sut.GetAllAsync();

        result[0].CanApplyAtSpaceLevel.ShouldBeFalse();
        result[0].CanApplyAtSystemLevel.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAll_SpaceRoleWithMixedPerms_SpaceOnly()
    {
        _userRoleDataProvider.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<UserRole>
        {
            new() { Id = 1, Name = "Space Owner", IsBuiltIn = true },
        });
        // SpaceOnly + Mixed → role scope determined by SpaceOnly, Mixed doesn't add system scope
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView", "TaskView" });

        var result = await _sut.GetAllAsync();

        result[0].CanApplyAtSpaceLevel.ShouldBeTrue();
        result[0].CanApplyAtSystemLevel.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAll_BothScopePerms_BothScopeFlags()
    {
        _userRoleDataProvider.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<UserRole>
        {
            new() { Id = 1, Name = "Custom Mixed", IsBuiltIn = false },
        });
        // Has both SpaceOnly (AccountView) and SystemOnly (AdministerSystem) → both flags true
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "AccountView", "AdministerSystem" });

        var result = await _sut.GetAllAsync();

        result[0].CanApplyAtSpaceLevel.ShouldBeTrue();
        result[0].CanApplyAtSystemLevel.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAll_OnlyMixedPerms_BothScopeFlags()
    {
        _userRoleDataProvider.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<UserRole>
        {
            new() { Id = 1, Name = "Task Only", IsBuiltIn = false },
        });
        // Only Mixed permissions → both flags true (fallback)
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "TaskView", "TeamView" });

        var result = await _sut.GetAllAsync();

        result[0].CanApplyAtSpaceLevel.ShouldBeTrue();
        result[0].CanApplyAtSystemLevel.ShouldBeTrue();
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
    public async Task AssignRoleToTeam_SpaceLevel_CreatesScoped()
    {
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 50, SpaceId = 1 };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView" });
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
    public async Task AssignRoleToTeam_SystemLevel_CreatesScoped()
    {
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 50, SpaceId = null };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "AdministerSystem", "UserView" });
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new UserRole { Id = 50, Name = "System Administrator" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        // P0-D.2: target role contains AdministerSystem (SystemOnly) — caller must themselves
        // hold AdministerSystem to assign it. Override the default-deny mock.
        _authorizationService
            .Setup(a => a.CheckPermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.AdministerSystem && r.SpaceId == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionCheckResult.Authorized());

        var result = await _sut.AssignRoleToTeamAsync(command);

        result.SpaceId.ShouldBeNull();
        _scopedUserRoleDataProvider.Verify(x => x.AddAsync(It.Is<ScopedUserRole>(r => r.SpaceId == null), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssignRoleToTeam_SystemOnlyRoleAtSpaceLevel_Throws()
    {
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 50, SpaceId = 1 };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "AdministerSystem", "UserView", "SpaceView" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.AssignRoleToTeamAsync(command));

        ex.Message.ShouldContain("system-level");
        _scopedUserRoleDataProvider.Verify(x => x.AddAsync(It.IsAny<ScopedUserRole>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AssignRoleToTeam_SpaceOnlyRoleAtSystemLevel_Throws()
    {
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 50, SpaceId = null };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "ProjectView", "DeploymentCreate" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.AssignRoleToTeamAsync(command));

        ex.Message.ShouldContain("space-level");
        _scopedUserRoleDataProvider.Verify(x => x.AddAsync(It.IsAny<ScopedUserRole>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveRoleFromTeam_CallsDelete()
    {
        await _sut.RemoveRoleFromTeamAsync(100);

        _scopedUserRoleDataProvider.Verify(x => x.DeleteAsync(100, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── P0-D.2: privilege-escalation guard on role assignment ──
    //
    // Pre-fix, a caller with TeamEdit (at any level) could attach a role containing
    // AdministerSystem (or any SystemOnly permission) to their own team — transitively
    // granting themselves full admin. The guard refuses unless the caller themselves
    // already holds AdministerSystem at system level.
    //
    // These tests pin every branch of the guard — without coverage a silent removal of
    // the check would re-open the full-admin privesc vector. Existing scope-validation
    // tests (AssignRoleToTeam_SpaceOnlyRoleAtSystemLevel_Throws etc.) remain green
    // because the guard runs AFTER scope validation.

    [Fact]
    public async Task AssignRoleToTeam_RoleContainsAdministerSystem_CallerNotAdmin_Throws()
    {
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 50, SpaceId = null };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { nameof(Permission.AdministerSystem) });
        // Default mock returns Denied for any permission check — caller is NOT an admin.

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AssignRoleToTeamAsync(command));

        ex.Message.ShouldContain("AdministerSystem",
            customMessage: "error must name the required caller-side permission");

        _scopedUserRoleDataProvider.Verify(
            x => x.AddAsync(It.IsAny<ScopedUserRole>(), true, It.IsAny<CancellationToken>()),
            Times.Never,
            failMessage: "no scoped role must be written when the guard rejects the request");
    }

    [Fact]
    public async Task AssignRoleToTeam_RoleContainsSystemOnlyPermission_CallerNotAdmin_Throws()
    {
        // Any SystemOnly permission (UserEdit, UserRoleEdit, SpaceEdit, …) triggers the
        // guard — not only the literal AdministerSystem. Otherwise the attacker just picks
        // a SystemOnly permission that's easier to bundle.
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 51, SpaceId = null };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(51, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { nameof(Permission.UserRoleEdit) });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AssignRoleToTeamAsync(command));

        ex.Message.ShouldContain("AdministerSystem",
            customMessage:
                "even SystemOnly permissions other than AdministerSystem must require admin " +
                "to assign — otherwise the attacker picks another SystemOnly permission");
    }

    [Fact]
    public async Task AssignRoleToTeam_RoleOnlyHasSpaceOnlyPermissions_CallerNotAdmin_Succeeds()
    {
        // Negative scope: a purely space-level role can be freely assigned — the guard
        // must only fire when a SystemOnly permission is present. Otherwise we'd break
        // every legitimate TeamEdit workflow in the product.
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 52, SpaceId = 1 };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(52, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { nameof(Permission.ProjectView), nameof(Permission.DeploymentCreate) });
        _userRoleDataProvider.Setup(x => x.GetByIdAsync(52, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRole { Id = 52, Name = "Deployer" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        await _sut.AssignRoleToTeamAsync(command);

        _scopedUserRoleDataProvider.Verify(
            x => x.AddAsync(It.Is<ScopedUserRole>(r => r.UserRoleId == 52), true, It.IsAny<CancellationToken>()),
            Times.Once);

        _authorizationService.Verify(
            a => a.CheckPermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            failMessage:
                "guard must short-circuit when role has no SystemOnly perms — otherwise we " +
                "burn a DB round-trip on every space-only assignment");
    }

    [Fact]
    public async Task AssignRoleToTeam_MixedPermissionsContainingSystemOnly_CallerNotAdmin_Throws()
    {
        // Defence-in-depth: a mixed role (some SpaceOnly + some SystemOnly) still contains
        // a SystemOnly perm. The guard must trigger — an attacker bundling AdministerSystem
        // with ProjectView mustn't sneak past.
        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 53, SpaceId = null };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(53, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>
            {
                nameof(Permission.ProjectView),
                nameof(Permission.AdministerSystem)
            });

        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AssignRoleToTeamAsync(command),
            customMessage: "mixed role containing SystemOnly perm must be rejected for non-admin callers");
    }

    [Fact]
    public async Task AssignRoleToTeam_NoAuthenticatedCaller_Throws()
    {
        // If ICurrentUser.Id is null (background job scope leaked in, DI misconfiguration,
        // test mode), the guard fails closed rather than defaulting to admin — a bad
        // default would itself be a privesc.
        _currentUser.Setup(u => u.Id).Returns((int?)null);

        var command = new AssignRoleToTeamCommand { TeamId = 10, UserRoleId = 50, SpaceId = null };

        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { nameof(Permission.AdministerSystem) });

        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AssignRoleToTeamAsync(command),
            customMessage: "guard must fail closed when caller identity is absent");
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
