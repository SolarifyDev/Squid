using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;

namespace Squid.UnitTests.Services.Authorization;

public class AuthorizationServiceTests
{
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly Mock<IScopedUserRoleDataProvider> _scopedUserRoleDataProvider = new();
    private readonly Mock<IUserRoleDataProvider> _userRoleDataProvider = new();
    private readonly AuthorizationService _sut;

    public AuthorizationServiceTests()
    {
        _sut = new AuthorizationService(_teamDataProvider.Object, _scopedUserRoleDataProvider.Object, _userRoleDataProvider.Object);
    }

    // ========== CheckPermissionAsync ==========

    [Fact]
    public async Task UserHasPermission_ReturnsAuthorized()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView", "ProjectEdit" });
        SetupEmptyScopes(100);

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task UserLacksPermission_ReturnsDenied()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectDelete, SpaceId = 1 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task NoTeams_ReturnsDenied()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 };

        SetupTeams(1, new List<int>());

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
        result.Reason.ShouldContain("not a member of any team");
    }

    [Fact]
    public async Task NoRolesAssigned_ReturnsDenied()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole>());

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
        result.Reason.ShouldContain("No roles assigned");
    }

    [Fact]
    public async Task ProjectScopeRestriction_AllowsMatchingProject()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1, ProjectId = 5 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 5, 6, 7 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task ProjectScopeRestriction_DeniesNonMatchingProject()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1, ProjectId = 99 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 5, 6, 7 });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task EnvironmentScopeRestriction_AllowsMatchingEnvironment()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.DeploymentCreate, SpaceId = 1, EnvironmentId = 3 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "DeploymentCreate" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 3, 4 });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task MultipleRoles_UnionOfPermissions()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.DeploymentCreate, SpaceId = 1 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null },
            new() { Id = 101, TeamId = 10, UserRoleId = 51, SpaceId = null },
        });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupPermissions(51, new List<string> { "DeploymentCreate" });
        SetupEmptyScopes(101);

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task EmptyScopeRestriction_AllowsAll()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1, ProjectId = 999 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupEmptyScopes(100);

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsurePermission_Denied_ThrowsPermissionDeniedException()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectDelete, SpaceId = 1 };

        SetupTeams(1, new List<int>());

        var ex = await Should.ThrowAsync<PermissionDeniedException>(() => _sut.EnsurePermissionAsync(request));

        ex.Permission.ShouldBe(Permission.ProjectDelete);
    }

    [Fact]
    public async Task SystemPermission_WithNullSpaceOnScopedRole_Authorized()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.AdministerSystem };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "AdministerSystem" });
        SetupEmptyScopes(100);

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task SpaceMismatch_ReturnsDenied()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 2 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 1 } });
        SetupPermissions(50, new List<string> { "ProjectView" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task MultipleTeams_UnionAcrossTeams()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.DeploymentCreate, SpaceId = 1 };

        SetupTeams(1, new List<int> { 10, 20 });
        SetupScopedRoles(new List<int> { 10, 20 }, new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null },
            new() { Id = 101, TeamId = 20, UserRoleId = 51, SpaceId = null },
        });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupPermissions(51, new List<string> { "DeploymentCreate" });
        SetupEmptyScopes(101);

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task ProjectGroupScopeRestriction_DeniesNonMatchingGroup()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1, ProjectGroupId = 99 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 1, 2 });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task SystemOnlyPermission_WithSpaceScopedRole_Denied()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.AdministerSystem };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 5 } });
        SetupPermissions(50, new List<string> { "AdministerSystem" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task MixedPermission_MatchesSpaceOrSystem()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.TaskView, SpaceId = 1 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 1 } });
        SetupPermissions(50, new List<string> { "TaskView" });
        SetupEmptyScopes(100);

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task MultipleRolesWithConflictingScopes_FirstMatchWins()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1, ProjectId = 2 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null },
            new() { Id = 101, TeamId = 10, UserRoleId = 51, SpaceId = null },
        });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupPermissions(51, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 1 });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 2, 3 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    // ========== AdministerSystem Bypass ==========

    [Theory]
    [InlineData(Permission.ProjectView, 1)]
    [InlineData(Permission.EnvironmentEdit, 1)]
    [InlineData(Permission.MachineDelete, 5)]
    public async Task AdministerSystem_BypassesSpaceLevelPermission(Permission permission, int spaceId)
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = permission, SpaceId = spaceId };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "AdministerSystem" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task AdministerSystem_InSpaceScopedRole_DoesNotBypass()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 2 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 1 } });
        SetupPermissions(50, new List<string> { "AdministerSystem" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task AdministerSystem_NoSpaceId_StillBypasses()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = null };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "AdministerSystem" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task AdministerSystem_IgnoresScopeRestrictions()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1, ProjectId = 99 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "AdministerSystem" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    // ========== GetResourceScopeAsync ==========

    [Fact]
    public async Task ResourceScope_NoTeams_ReturnsNone()
    {
        SetupTeams(1, new List<int>());

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeFalse();
        result.IsProjectUnrestricted.ShouldBeFalse();
        result.IsEnvironmentUnrestricted.ShouldBeFalse();
        result.IsProjectGroupUnrestricted.ShouldBeFalse();
    }

    [Fact]
    public async Task ResourceScope_NoRoles_ReturnsNone()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole>());

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeFalse();
        result.IsProjectUnrestricted.ShouldBeFalse();
        result.IsEnvironmentUnrestricted.ShouldBeFalse();
        result.IsProjectGroupUnrestricted.ShouldBeFalse();
    }

    [Fact]
    public async Task ResourceScope_Admin_ReturnsUnrestricted()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "AdministerSystem" });

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeTrue();
    }

    [Fact]
    public async Task ResourceScope_NoRestrictions_ReturnsUnrestricted()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupEmptyScopes(100);

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeTrue();
    }

    [Fact]
    public async Task ResourceScope_ProjectRestriction_ReturnsFiltered()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 5, 6 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeFalse();
        result.IsProjectUnrestricted.ShouldBeFalse();
        result.ProjectIds.ShouldBe(new HashSet<int> { 5, 6 });
        result.IsEnvironmentUnrestricted.ShouldBeTrue();
        result.IsProjectGroupUnrestricted.ShouldBeTrue();
    }

    [Fact]
    public async Task ResourceScope_EnvironmentRestriction_ReturnsFiltered()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "EnvironmentView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 3, 4 });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.EnvironmentView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeFalse();
        result.IsEnvironmentUnrestricted.ShouldBeFalse();
        result.EnvironmentIds.ShouldBe(new HashSet<int> { 3, 4 });
        result.IsProjectUnrestricted.ShouldBeTrue();
    }

    [Fact]
    public async Task ResourceScope_MultipleRoles_UnionScopes()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null },
            new() { Id = 101, TeamId = 10, UserRoleId = 51, SpaceId = null },
        });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupPermissions(51, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 1, 2 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 3, 4 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(101, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeFalse();
        result.IsProjectUnrestricted.ShouldBeFalse();
        result.ProjectIds.ShouldBe(new HashSet<int> { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task ResourceScope_OneRoleUnrestricted_DimensionUnrestricted()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null },
            new() { Id = 101, TeamId = 10, UserRoleId = 51, SpaceId = null },
        });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupPermissions(51, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 1 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        SetupEmptyScopes(101);

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeTrue();
    }

    [Fact]
    public async Task ResourceScope_PermissionNotGranted_ReturnsNone()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectEdit" });

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeFalse();
        result.IsProjectUnrestricted.ShouldBeFalse();
        result.IsEnvironmentUnrestricted.ShouldBeFalse();
        result.IsProjectGroupUnrestricted.ShouldBeFalse();
    }

    [Fact]
    public async Task ResourceScope_MixedDimensions()
    {
        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 1, 2 });
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await _sut.GetResourceScopeAsync(new ResourceScopeRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 1 });

        result.IsUnrestricted.ShouldBeFalse();
        result.IsProjectUnrestricted.ShouldBeFalse();
        result.ProjectIds.ShouldBe(new HashSet<int> { 1, 2 });
        result.IsEnvironmentUnrestricted.ShouldBeTrue();
        result.IsProjectGroupUnrestricted.ShouldBeTrue();
    }

    // ========== Cross-Space Denial ==========

    [Fact]
    public async Task SpaceScopedRole_WrongSpace_Denied()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 2 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 1 } });
        SetupPermissions(50, new List<string> { "ProjectView" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
        result.Reason.ShouldContain("No roles match");
    }

    [Fact]
    public async Task MixedPermission_SpaceScopedRole_NullSpaceId_Denied()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.TaskView, SpaceId = null };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 5 } });
        SetupPermissions(50, new List<string> { "TaskView" });

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task SpaceScopedRole_SystemNullRole_MatchesAnySpace()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectView, SpaceId = 99 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = null } });
        SetupPermissions(50, new List<string> { "ProjectView" });
        SetupEmptyScopes(100);

        var result = await _sut.CheckPermissionAsync(request);

        result.IsAuthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsurePermission_Denied_ThrowsWithCorrectPermission()
    {
        var request = new PermissionCheckRequest { UserId = 1, Permission = Permission.ProjectDelete, SpaceId = 1 };

        SetupTeams(1, new List<int> { 10 });
        SetupScopedRoles(new List<int> { 10 }, new List<ScopedUserRole> { new() { Id = 100, TeamId = 10, UserRoleId = 50, SpaceId = 1 } });
        SetupPermissions(50, new List<string> { "ProjectView" });

        var ex = await Should.ThrowAsync<PermissionDeniedException>(_sut.EnsurePermissionAsync(request));

        ex.Permission.ShouldBe(Permission.ProjectDelete);
    }

    // ========== Helpers ==========

    private void SetupTeams(int userId, List<int> teamIds)
    {
        _teamDataProvider.Setup(x => x.GetTeamIdsByUserIdAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(teamIds);
    }

    private void SetupScopedRoles(List<int> teamIds, List<ScopedUserRole> scopedRoles)
    {
        _scopedUserRoleDataProvider.Setup(x => x.GetByTeamIdsAsync(teamIds, It.IsAny<CancellationToken>())).ReturnsAsync(scopedRoles);
    }

    private void SetupPermissions(int userRoleId, List<string> permissions)
    {
        _userRoleDataProvider.Setup(x => x.GetPermissionsAsync(userRoleId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);
    }

    private void SetupEmptyScopes(int scopedUserRoleId)
    {
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectScopeAsync(scopedUserRoleId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetEnvironmentScopeAsync(scopedUserRoleId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedUserRoleDataProvider.Setup(x => x.GetProjectGroupScopeAsync(scopedUserRoleId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
    }
}
