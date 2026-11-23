using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Mcp;

public class ScopedReadAuthorizationTests : TestBase
{
    public ScopedReadAuthorizationTests()
        : base("ScopedReadAuthorization", "squid_it_mcp_scoped_read_authorization")
    {
    }

    [Fact]
    public async Task Grants_PreserveEachRolesDimensions()
    {
        const int userId = 700;

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 1,
                roleName: "ProjectAGrant",
                Permission.ProjectView,
                roleSpaceId: 1,
                projectIds: [101],
                environmentIds: [201],
                projectGroupIds: [301]).ConfigureAwait(false);

            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 1,
                roleName: "ProjectBGrant",
                Permission.ProjectView,
                roleSpaceId: 1,
                projectIds: [102],
                environmentIds: [202],
                projectGroupIds: []).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.ProjectView).ConfigureAwait(false);

            grants.Count.ShouldBe(2);

            var projectAGrant = grants.Single(g => g.ProjectIds.SetEquals(new HashSet<int> { 101 }));
            projectAGrant.SpaceId.ShouldBe(1);
            projectAGrant.EnvironmentIds.ShouldBe(new HashSet<int> { 201 });
            projectAGrant.ProjectGroupIds.ShouldBe(new HashSet<int> { 301 });

            var projectBGrant = grants.Single(g => g.ProjectIds.SetEquals(new HashSet<int> { 102 }));
            projectBGrant.SpaceId.ShouldBe(1);
            projectBGrant.EnvironmentIds.ShouldBe(new HashSet<int> { 202 });
            projectBGrant.ProjectGroupIds.ShouldBeEmpty();

            grants.Any(g => g.ProjectIds.SetEquals(new HashSet<int> { 101 }) && g.EnvironmentIds.SetEquals(new HashSet<int> { 202 })).ShouldBeFalse();
            grants.Any(g => g.ProjectIds.SetEquals(new HashSet<int> { 102 }) && g.EnvironmentIds.SetEquals(new HashSet<int> { 201 })).ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Grants_SystemOnlyPermission_RejectsSpaceRoles()
    {
        const int userId = 701;

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 0,
                roleName: "SystemSpaceViewer",
                Permission.SpaceView,
                roleSpaceId: null,
                projectIds: [111],
                environmentIds: [211],
                projectGroupIds: [311]).ConfigureAwait(false);

            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 1,
                roleName: "SpaceScopedSpaceViewer",
                Permission.SpaceView,
                roleSpaceId: 1,
                projectIds: [112],
                environmentIds: [212],
                projectGroupIds: [312]).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.SpaceView).ConfigureAwait(false);

            grants.Count.ShouldBe(1);
            var grant = grants[0];
            grant.SpaceId.ShouldBeNull();
            grant.ProjectIds.ShouldBe(new HashSet<int> { 111 });
            grant.EnvironmentIds.ShouldBe(new HashSet<int> { 211 });
            grant.ProjectGroupIds.ShouldBe(new HashSet<int> { 311 });
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Grants_TaskView_ReturnsSystemAndSpaceRoleGrants()
    {
        const int userId = 705;

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 0,
                roleName: "SystemTaskViewer",
                Permission.TaskView,
                roleSpaceId: null,
                projectIds: [151],
                environmentIds: [251],
                projectGroupIds: [351]).ConfigureAwait(false);

            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 1,
                roleName: "SpaceTaskViewer",
                Permission.TaskView,
                roleSpaceId: 1,
                projectIds: [152],
                environmentIds: [252],
                projectGroupIds: [352]).ConfigureAwait(false);
        });

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.TaskView).ConfigureAwait(false);

            grants.Count.ShouldBe(2);

            var systemGrant = grants.Single(grant => grant.SpaceId == null);
            systemGrant.ProjectIds.ShouldBe(new HashSet<int> { 151 });
            systemGrant.EnvironmentIds.ShouldBe(new HashSet<int> { 251 });
            systemGrant.ProjectGroupIds.ShouldBe(new HashSet<int> { 351 });

            var spaceGrant = grants.Single(grant => grant.SpaceId == 1);
            spaceGrant.ProjectIds.ShouldBe(new HashSet<int> { 152 });
            spaceGrant.EnvironmentIds.ShouldBe(new HashSet<int> { 252 });
            spaceGrant.ProjectGroupIds.ShouldBe(new HashSet<int> { 352 });
        });
    }

    [Fact]
    public async Task Grants_NoTeamMembership_ReturnsEmpty()
    {
        const int userId = 706;

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.TaskView).ConfigureAwait(false);

            grants.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Grants_TeamWithoutRoles_ReturnsEmpty()
    {
        const int userId = 707;

        await Run<ITeamDataProvider>(async teamProvider =>
        {
            var team = new Team { Name = "TeamWithoutRoles", SpaceId = 1 };

            await teamProvider.AddAsync(team).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId }).ConfigureAwait(false);
        });

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.TaskView).ConfigureAwait(false);

            grants.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Grants_NoMatchingPermission_ReturnsEmpty()
    {
        const int userId = 702;

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 1,
                roleName: "ProjectViewer",
                Permission.ProjectView,
                roleSpaceId: 1,
                projectIds: [121],
                environmentIds: [221],
                projectGroupIds: [321]).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.ReleaseView).ConfigureAwait(false);

            grants.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Grants_SystemAdministrator_ReturnsSingleUnrestrictedGrant()
    {
        const int userId = 703;

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 0,
                roleName: "SystemAdministrator",
                Permission.AdministerSystem,
                roleSpaceId: null,
                projectIds: [131],
                environmentIds: [231],
                projectGroupIds: [331]).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.ProjectView).ConfigureAwait(false);

            grants.Count.ShouldBe(1);
            var grant = grants[0];
            grant.SpaceId.ShouldBeNull();
            grant.ProjectIds.ShouldBeEmpty();
            grant.EnvironmentIds.ShouldBeEmpty();
            grant.ProjectGroupIds.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Grants_SpaceScopedAdministrator_DoesNotBypass()
    {
        const int userId = 704;

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            await AddRoleGrantAsync(
                teamProvider,
                roleProvider,
                scopedProvider,
                userId,
                teamSpaceId: 1,
                roleName: "SpaceScopedAdministrator",
                Permission.AdministerSystem,
                roleSpaceId: 1,
                projectIds: [141],
                environmentIds: [241],
                projectGroupIds: [341]).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IAuthorizationService>(async authService =>
        {
            var grants = await authService.GetResourceScopeGrantsAsync(userId, Permission.ProjectView).ConfigureAwait(false);

            grants.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    private static async Task AddRoleGrantAsync(
        ITeamDataProvider teamProvider,
        IUserRoleDataProvider roleProvider,
        IScopedUserRoleDataProvider scopedProvider,
        int userId,
        int teamSpaceId,
        string roleName,
        Permission permission,
        int? roleSpaceId,
        IReadOnlyCollection<int> projectIds,
        IReadOnlyCollection<int> environmentIds,
        IReadOnlyCollection<int> projectGroupIds)
    {
        var team = new Team { Name = $"{roleName}Team", SpaceId = teamSpaceId };
        await teamProvider.AddAsync(team).ConfigureAwait(false);
        await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId }).ConfigureAwait(false);

        var role = new UserRole { Name = roleName, IsBuiltIn = false };
        await roleProvider.AddAsync(role).ConfigureAwait(false);
        await roleProvider.SetPermissionsAsync(role.Id, new List<string> { permission.ToString() }).ConfigureAwait(false);

        var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = roleSpaceId };
        await scopedProvider.AddAsync(scopedRole).ConfigureAwait(false);

        if (projectIds.Count > 0)
            await scopedProvider.SetProjectScopeAsync(scopedRole.Id, projectIds.ToList()).ConfigureAwait(false);

        if (environmentIds.Count > 0)
            await scopedProvider.SetEnvironmentScopeAsync(scopedRole.Id, environmentIds.ToList()).ConfigureAwait(false);

        if (projectGroupIds.Count > 0)
            await scopedProvider.SetProjectGroupScopeAsync(scopedRole.Id, projectGroupIds.ToList()).ConfigureAwait(false);
    }
}
