using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Teams;

namespace Squid.IntegrationTests.Services.Account;

public class TeamDataProviderTests : TestBase
{
    public TeamDataProviderTests()
        : base("TeamDataProvider", "squid_it_team_data_provider")
    {
    }

    // ========== CRUD ==========

    [Fact]
    public async Task Add_ThenGetById_ReturnsTeam()
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = new Team { Name = "Ops", Description = "Operations", SpaceId = 1 };
            await provider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = await provider.GetByIdAsync(teamId).ConfigureAwait(false);

            team.ShouldNotBeNull();
            team.Name.ShouldBe("Ops");
            team.Description.ShouldBe("Operations");
            team.SpaceId.ShouldBe(1);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Add_SetsAuditColumns()
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = new Team { Name = "Audit Test", SpaceId = 1 };
            await provider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = await provider.GetByIdAsync(teamId).ConfigureAwait(false);

            team.CreatedDate.ShouldNotBe(default);
            team.LastModifiedDate.ShouldNotBe(default);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Update_PersistsChanges()
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = new Team { Name = "Original", SpaceId = 1 };
            await provider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = await provider.GetByIdAsync(teamId).ConfigureAwait(false);
            team.Name = "Updated";
            team.Description = "New desc";
            await provider.UpdateAsync(team).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = await provider.GetByIdAsync(teamId).ConfigureAwait(false);

            team.Name.ShouldBe("Updated");
            team.Description.ShouldBe("New desc");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Delete_RemovesTeam()
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = new Team { Name = "ToDelete", SpaceId = 1 };
            await provider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = await provider.GetByIdAsync(teamId).ConfigureAwait(false);
            await provider.DeleteAsync(team).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = await provider.GetByIdAsync(teamId).ConfigureAwait(false);
            team.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    // ========== Queries ==========

    [Fact]
    public async Task GetByIds_ReturnsMatchingOnly()
    {
        var ids = new List<int>();

        await Run<ITeamDataProvider>(async provider =>
        {
            var t1 = new Team { Name = "A", SpaceId = 1 };
            var t2 = new Team { Name = "B", SpaceId = 1 };
            var t3 = new Team { Name = "C", SpaceId = 1 };
            await provider.AddAsync(t1).ConfigureAwait(false);
            await provider.AddAsync(t2).ConfigureAwait(false);
            await provider.AddAsync(t3).ConfigureAwait(false);
            ids.AddRange(new[] { t1.Id, t2.Id, t3.Id });
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var result = await provider.GetByIdsAsync(new List<int> { ids[0], ids[2] }).ConfigureAwait(false);

            result.Count.ShouldBe(2);
            result.ShouldContain(t => t.Name == "A");
            result.ShouldContain(t => t.Name == "C");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetAllBySpace_FiltersCorrectly()
    {
        await Run<ITeamDataProvider>(async provider =>
        {
            await provider.AddAsync(new Team { Name = "Space1-A", SpaceId = 1 }).ConfigureAwait(false);
            await provider.AddAsync(new Team { Name = "Space1-B", SpaceId = 1 }).ConfigureAwait(false);
            await provider.AddAsync(new Team { Name = "Space2-A", SpaceId = 2 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var space1 = await provider.GetAllBySpaceAsync(1).ConfigureAwait(false);
            var space2 = await provider.GetAllBySpaceAsync(2).ConfigureAwait(false);

            space1.Count.ShouldBe(2);
            space2.Count.ShouldBe(1);
            space2.Single().Name.ShouldBe("Space2-A");
        }).ConfigureAwait(false);
    }

    // ========== Members ==========

    [Fact]
    public async Task AddMember_ThenGetMembers_ReturnsMember()
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = new Team { Name = "WithMembers", SpaceId = 1 };
            await provider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;

            await provider.AddMemberAsync(new TeamMember { TeamId = teamId, UserId = 10 }).ConfigureAwait(false);
            await provider.AddMemberAsync(new TeamMember { TeamId = teamId, UserId = 20 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var members = await provider.GetMembersByTeamIdAsync(teamId).ConfigureAwait(false);

            members.Count.ShouldBe(2);
            members.ShouldContain(m => m.UserId == 10);
            members.ShouldContain(m => m.UserId == 20);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task RemoveMember_RemovesOnlyTargetMember()
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = new Team { Name = "RemoveTest", SpaceId = 1 };
            await provider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;

            await provider.AddMemberAsync(new TeamMember { TeamId = teamId, UserId = 10 }).ConfigureAwait(false);
            await provider.AddMemberAsync(new TeamMember { TeamId = teamId, UserId = 20 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            await provider.RemoveMemberAsync(new TeamMember { TeamId = teamId, UserId = 10 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var members = await provider.GetMembersByTeamIdAsync(teamId).ConfigureAwait(false);

            members.Count.ShouldBe(1);
            members.Single().UserId.ShouldBe(20);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetTeamIdsByUserId_ReturnsAllTeamsForUser()
    {
        await Run<ITeamDataProvider>(async provider =>
        {
            var t1 = new Team { Name = "Team1", SpaceId = 1 };
            var t2 = new Team { Name = "Team2", SpaceId = 1 };
            var t3 = new Team { Name = "Team3", SpaceId = 1 };
            await provider.AddAsync(t1).ConfigureAwait(false);
            await provider.AddAsync(t2).ConfigureAwait(false);
            await provider.AddAsync(t3).ConfigureAwait(false);

            await provider.AddMemberAsync(new TeamMember { TeamId = t1.Id, UserId = 42 }).ConfigureAwait(false);
            await provider.AddMemberAsync(new TeamMember { TeamId = t3.Id, UserId = 42 }).ConfigureAwait(false);
            await provider.AddMemberAsync(new TeamMember { TeamId = t2.Id, UserId = 99 }).ConfigureAwait(false);

            var teamIds = await provider.GetTeamIdsByUserIdAsync(42).ConfigureAwait(false);

            teamIds.Count.ShouldBe(2);
            teamIds.ShouldContain(t1.Id);
            teamIds.ShouldContain(t3.Id);
        }).ConfigureAwait(false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsUserInAnyTeam_ReturnsCorrectResult(bool shouldBeInTeam)
    {
        await Run<ITeamDataProvider>(async provider =>
        {
            var t1 = new Team { Name = "Alpha", SpaceId = 1 };
            var t2 = new Team { Name = "Beta", SpaceId = 1 };
            await provider.AddAsync(t1).ConfigureAwait(false);
            await provider.AddAsync(t2).ConfigureAwait(false);

            if (shouldBeInTeam)
                await provider.AddMemberAsync(new TeamMember { TeamId = t1.Id, UserId = 42 }).ConfigureAwait(false);

            var result = await provider.IsUserInAnyTeamAsync(42, new List<int> { t1.Id, t2.Id }).ConfigureAwait(false);

            result.ShouldBe(shouldBeInTeam);
        }).ConfigureAwait(false);
    }

    // ========== Cascade Delete ==========

    [Fact]
    public async Task DeleteTeam_CascadeDeletesMembers()
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = new Team { Name = "Cascade", SpaceId = 1 };
            await provider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;

            await provider.AddMemberAsync(new TeamMember { TeamId = teamId, UserId = 10 }).ConfigureAwait(false);
            await provider.AddMemberAsync(new TeamMember { TeamId = teamId, UserId = 20 }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var team = await provider.GetByIdAsync(teamId).ConfigureAwait(false);
            await provider.DeleteAsync(team).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<ITeamDataProvider>(async provider =>
        {
            var members = await provider.GetMembersByTeamIdAsync(teamId).ConfigureAwait(false);
            members.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }
}
