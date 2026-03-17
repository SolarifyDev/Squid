using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;

namespace Squid.IntegrationTests.Services.Authorization;

public class ScopedUserRoleDataProviderTests : TestBase
{
    public ScopedUserRoleDataProviderTests()
        : base("ScopedUserRoleDataProvider", "squid_it_scoped_user_role_data_provider")
    {
    }

    private async Task<(Team team, UserRole role)> SeedTeamAndRoleAsync()
    {
        Team team = null;
        UserRole role = null;

        await Run<ITeamDataProvider, IUserRoleDataProvider>(async (teamProvider, roleProvider) =>
        {
            team = new Team { Name = "TestTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);

            role = new UserRole { Name = $"TestRole_{Guid.NewGuid():N}", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return (team, role);
    }

    [Fact]
    public async Task GetByTeamIds_ReturnsMatchingRoles()
    {
        var (team, role) = await SeedTeamAndRoleAsync().ConfigureAwait(false);

        await Run<IScopedUserRoleDataProvider>(async provider =>
        {
            await provider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = 1 }).ConfigureAwait(false);

            var results = await provider.GetByTeamIdsAsync(new List<int> { team.Id }).ConfigureAwait(false);
            results.Count.ShouldBe(1);
            results[0].TeamId.ShouldBe(team.Id);
            results[0].UserRoleId.ShouldBe(role.Id);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SetProjectScope_PersistsCorrectly()
    {
        var (team, role) = await SeedTeamAndRoleAsync().ConfigureAwait(false);

        await Run<IScopedUserRoleDataProvider>(async provider =>
        {
            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id };
            await provider.AddAsync(scopedRole).ConfigureAwait(false);

            await provider.SetProjectScopeAsync(scopedRole.Id, new List<int> { 10, 20, 30 }).ConfigureAwait(false);

            var projectIds = await provider.GetProjectScopeAsync(scopedRole.Id).ConfigureAwait(false);
            projectIds.Count.ShouldBe(3);
            projectIds.ShouldContain(10);
            projectIds.ShouldContain(20);
            projectIds.ShouldContain(30);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SetEnvironmentScope_PersistsCorrectly()
    {
        var (team, role) = await SeedTeamAndRoleAsync().ConfigureAwait(false);

        await Run<IScopedUserRoleDataProvider>(async provider =>
        {
            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id };
            await provider.AddAsync(scopedRole).ConfigureAwait(false);

            await provider.SetEnvironmentScopeAsync(scopedRole.Id, new List<int> { 5, 6 }).ConfigureAwait(false);

            var envIds = await provider.GetEnvironmentScopeAsync(scopedRole.Id).ConfigureAwait(false);
            envIds.Count.ShouldBe(2);
            envIds.ShouldContain(5);
            envIds.ShouldContain(6);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SetProjectGroupScope_PersistsCorrectly()
    {
        var (team, role) = await SeedTeamAndRoleAsync().ConfigureAwait(false);

        await Run<IScopedUserRoleDataProvider>(async provider =>
        {
            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id };
            await provider.AddAsync(scopedRole).ConfigureAwait(false);

            await provider.SetProjectGroupScopeAsync(scopedRole.Id, new List<int> { 100 }).ConfigureAwait(false);

            var groupIds = await provider.GetProjectGroupScopeAsync(scopedRole.Id).ConfigureAwait(false);
            groupIds.Count.ShouldBe(1);
            groupIds.ShouldContain(100);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Delete_RemovesScopedRole()
    {
        var (team, role) = await SeedTeamAndRoleAsync().ConfigureAwait(false);

        await Run<IScopedUserRoleDataProvider>(async provider =>
        {
            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id };
            await provider.AddAsync(scopedRole).ConfigureAwait(false);

            await provider.DeleteAsync(scopedRole).ConfigureAwait(false);

            var results = await provider.GetByTeamIdsAsync(new List<int> { team.Id }).ConfigureAwait(false);
            results.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SetProjectScope_ReplacesExisting()
    {
        var (team, role) = await SeedTeamAndRoleAsync().ConfigureAwait(false);

        await Run<IScopedUserRoleDataProvider>(async provider =>
        {
            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id };
            await provider.AddAsync(scopedRole).ConfigureAwait(false);

            await provider.SetProjectScopeAsync(scopedRole.Id, new List<int> { 1, 2, 3 }).ConfigureAwait(false);
            await provider.SetProjectScopeAsync(scopedRole.Id, new List<int> { 10, 20 }).ConfigureAwait(false);

            var projectIds = await provider.GetProjectScopeAsync(scopedRole.Id).ConfigureAwait(false);
            projectIds.Count.ShouldBe(2);
            projectIds.ShouldContain(10);
            projectIds.ShouldContain(20);
            projectIds.ShouldNotContain(1);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetByTeamIds_MultipleTeams_ReturnsCombined()
    {
        Team team2 = null;
        UserRole role2 = null;
        var (team1, role1) = await SeedTeamAndRoleAsync().ConfigureAwait(false);

        await Run<ITeamDataProvider, IUserRoleDataProvider>(async (teamProvider, roleProvider) =>
        {
            team2 = new Team { Name = "TestTeam2", SpaceId = 1 };
            await teamProvider.AddAsync(team2).ConfigureAwait(false);

            role2 = new UserRole { Name = $"TestRole2_{Guid.NewGuid():N}", IsBuiltIn = false };
            await roleProvider.AddAsync(role2).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IScopedUserRoleDataProvider>(async provider =>
        {
            await provider.AddAsync(new ScopedUserRole { TeamId = team1.Id, UserRoleId = role1.Id }).ConfigureAwait(false);
            await provider.AddAsync(new ScopedUserRole { TeamId = team2.Id, UserRoleId = role2.Id }).ConfigureAwait(false);

            var results = await provider.GetByTeamIdsAsync(new List<int> { team1.Id, team2.Id }).ConfigureAwait(false);
            results.Count.ShouldBe(2);
        }).ConfigureAwait(false);
    }
}
