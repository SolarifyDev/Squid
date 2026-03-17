using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;

namespace Squid.IntegrationTests.Services.Authorization;

public class AuthorizationServiceIntegrationTests : TestBase
{
    public AuthorizationServiceIntegrationTests()
        : base("AuthorizationService", "squid_it_authorization_service")
    {
    }

    [Fact]
    public async Task FullChain_UserInTeamWithRole_HasPermission()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "DevTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 42 }).ConfigureAwait(false);

            var role = new UserRole { Name = "Dev", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView", "ProjectEdit" }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = 1 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var result = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 42,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);

            result.IsAuthorized.ShouldBeTrue();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_UserNotInTeam_Denied()
    {
        await Run<IAuthorizationService>(async authService =>
        {
            var result = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 999,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);

            result.IsAuthorized.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_ScopeRestriction_FiltersCorrectly()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "ScopeTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 50 }).ConfigureAwait(false);

            var role = new UserRole { Name = "ScopedDev", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);

            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = 1 };
            await scopedProvider.AddAsync(scopedRole).ConfigureAwait(false);
            await scopedProvider.SetProjectScopeAsync(scopedRole.Id, new List<int> { 100 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var allowed = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 50,
                Permission = Permission.ProjectView,
                SpaceId = 1,
                ProjectId = 100,
            }).ConfigureAwait(false);
            allowed.IsAuthorized.ShouldBeTrue();

            var denied = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 50,
                Permission = Permission.ProjectView,
                SpaceId = 1,
                ProjectId = 200,
            }).ConfigureAwait(false);
            denied.IsAuthorized.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_MultipleTeams_UnionOfPermissions()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team1 = new Team { Name = "ViewTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team1).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team1.Id, UserId = 60 }).ConfigureAwait(false);

            var team2 = new Team { Name = "DeployTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team2).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team2.Id, UserId = 60 }).ConfigureAwait(false);

            var viewerRole = new UserRole { Name = "Viewer", IsBuiltIn = false };
            await roleProvider.AddAsync(viewerRole).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(viewerRole.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);

            var deployerRole = new UserRole { Name = "Deployer", IsBuiltIn = false };
            await roleProvider.AddAsync(deployerRole).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(deployerRole.Id, new List<string> { "DeploymentCreate" }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team1.Id, UserRoleId = viewerRole.Id, SpaceId = 1 }).ConfigureAwait(false);
            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team2.Id, UserRoleId = deployerRole.Id, SpaceId = 1 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var viewResult = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 60,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);
            viewResult.IsAuthorized.ShouldBeTrue();

            var deployResult = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 60,
                Permission = Permission.DeploymentCreate,
                SpaceId = 1,
            }).ConfigureAwait(false);
            deployResult.IsAuthorized.ShouldBeTrue();

            var deleteResult = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 60,
                Permission = Permission.ProjectDelete,
                SpaceId = 1,
            }).ConfigureAwait(false);
            deleteResult.IsAuthorized.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_SpaceMismatch_Denied()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "SpaceTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 70 }).ConfigureAwait(false);

            var role = new UserRole { Name = "SpaceDev", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = 1 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var allowed = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 70,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);
            allowed.IsAuthorized.ShouldBeTrue();

            var denied = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 70,
                Permission = Permission.ProjectView,
                SpaceId = 99,
            }).ConfigureAwait(false);
            denied.IsAuthorized.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    // ========== AdministerSystem Bypass ==========

    [Fact]
    public async Task FullChain_AdministerSystem_BypassesSpacePermission()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "AdminTeam", SpaceId = 0 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 80 }).ConfigureAwait(false);

            var role = new UserRole { Name = "SysAdmin", IsBuiltIn = true };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "AdministerSystem" }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = null }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var result = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 80,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);

            result.IsAuthorized.ShouldBeTrue();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_AdministerSystem_SpaceScoped_DoesNotBypass()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "SpaceAdminTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 81 }).ConfigureAwait(false);

            var role = new UserRole { Name = "SpaceAdmin", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "AdministerSystem" }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = 1 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var result = await authService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = 81,
                Permission = Permission.ProjectView,
                SpaceId = 2,
            }).ConfigureAwait(false);

            result.IsAuthorized.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    // ========== GetResourceScopeAsync ==========

    [Fact]
    public async Task FullChain_ResourceScope_NoRestrictions_Unrestricted()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "UnrestrictedTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 90 }).ConfigureAwait(false);

            var role = new UserRole { Name = "UnrestrictedDev", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = 1 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var scope = await authService.GetResourceScopeAsync(new ResourceScopeRequest
            {
                UserId = 90,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);

            scope.IsUnrestricted.ShouldBeTrue();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_ResourceScope_ProjectRestriction()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "RestrictedTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 91 }).ConfigureAwait(false);

            var role = new UserRole { Name = "RestrictedDev", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);

            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = 1 };
            await scopedProvider.AddAsync(scopedRole).ConfigureAwait(false);
            await scopedProvider.SetProjectScopeAsync(scopedRole.Id, new List<int> { 100, 200 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var scope = await authService.GetResourceScopeAsync(new ResourceScopeRequest
            {
                UserId = 91,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);

            scope.IsUnrestricted.ShouldBeFalse();
            scope.IsProjectUnrestricted.ShouldBeFalse();
            scope.ProjectIds.ShouldBe(new HashSet<int> { 100, 200 });
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_ResourceScope_MultipleRoles_Union()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "UnionTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 92 }).ConfigureAwait(false);

            var role1 = new UserRole { Name = "Viewer1", IsBuiltIn = false };
            await roleProvider.AddAsync(role1).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role1.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);

            var role2 = new UserRole { Name = "Viewer2", IsBuiltIn = false };
            await roleProvider.AddAsync(role2).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role2.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);

            var scoped1 = new ScopedUserRole { TeamId = team.Id, UserRoleId = role1.Id, SpaceId = 1 };
            await scopedProvider.AddAsync(scoped1).ConfigureAwait(false);
            await scopedProvider.SetProjectScopeAsync(scoped1.Id, new List<int> { 100 }).ConfigureAwait(false);

            var scoped2 = new ScopedUserRole { TeamId = team.Id, UserRoleId = role2.Id, SpaceId = 1 };
            await scopedProvider.AddAsync(scoped2).ConfigureAwait(false);
            await scopedProvider.SetProjectScopeAsync(scoped2.Id, new List<int> { 200 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var scope = await authService.GetResourceScopeAsync(new ResourceScopeRequest
            {
                UserId = 92,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);

            scope.IsUnrestricted.ShouldBeFalse();
            scope.ProjectIds.ShouldBe(new HashSet<int> { 100, 200 });
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullChain_ResourceScope_Admin_Unrestricted()
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = new Team { Name = "AdminScopeTeam", SpaceId = 0 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 93 }).ConfigureAwait(false);

            var role = new UserRole { Name = "SysAdminScope", IsBuiltIn = true };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { "AdministerSystem" }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = null }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var scope = await authService.GetResourceScopeAsync(new ResourceScopeRequest
            {
                UserId = 93,
                Permission = Permission.ProjectView,
                SpaceId = 1,
            }).ConfigureAwait(false);

            scope.IsUnrestricted.ShouldBeTrue();
        }).ConfigureAwait(false);
    }
}
