using System;
using System.Linq.Expressions;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Teams;
using Squid.Message.Models.Teams;

namespace Squid.UnitTests.Services.Teams;

public class TeamServiceTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IScopedUserRoleDataProvider> _scopedUserRoleDataProvider = new();
    private readonly TeamService _sut;

    public TeamServiceTests()
    {
        _sut = new TeamService(_mapper.Object, _teamDataProvider.Object, _repository.Object, _scopedUserRoleDataProvider.Object);
    }

    [Fact]
    public async Task Create_MapsAndCallsDataProvider()
    {
        var command = new CreateTeamCommand { Name = "Ops", Description = "Ops team", SpaceId = 1 };
        var team = new Team { Id = 1, Name = "Ops" };
        var dto = new TeamDto { Id = 1, Name = "Ops" };

        _mapper.Setup(m => m.Map<Team>(command)).Returns(team);
        _mapper.Setup(m => m.Map<TeamDto>(team)).Returns(dto);

        var result = await _sut.CreateAsync(command);

        result.ShouldBe(dto);
        _teamDataProvider.Verify(p => p.AddAsync(team, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_ExistingTeam_MapsAndUpdates()
    {
        var command = new UpdateTeamCommand { Id = 1, Name = "Ops v2", SpaceId = 1 };
        var team = new Team { Id = 1, Name = "Ops" };
        var dto = new TeamDto { Id = 1, Name = "Ops v2" };

        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);
        _mapper.Setup(m => m.Map(command, team)).Returns(team);
        _mapper.Setup(m => m.Map<TeamDto>(team)).Returns(dto);

        var result = await _sut.UpdateAsync(command);

        result.ShouldBe(dto);
        _teamDataProvider.Verify(p => p.UpdateAsync(team, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        _teamDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Team)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.UpdateAsync(new UpdateTeamCommand { Id = 99 }));
    }

    [Fact]
    public async Task Delete_ExistingTeam_CascadesScopedUserRolesAndMembers()
    {
        var team = new Team { Id = 1 };
        var scopedRoles = new List<ScopedUserRole>
        {
            new() { Id = 10, TeamId = 1, UserRoleId = 5, SpaceId = 1 },
            new() { Id = 11, TeamId = 1, UserRoleId = 6, SpaceId = 2 },
        };

        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);
        _scopedUserRoleDataProvider.Setup(p => p.GetByTeamIdsAsync(It.Is<List<int>>(ids => ids.Contains(1)), It.IsAny<CancellationToken>())).ReturnsAsync(scopedRoles);

        await _sut.DeleteAsync(1);

        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProject>(It.IsAny<Expression<Func<ScopedUserRoleProject, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleEnvironment>(It.IsAny<Expression<Func<ScopedUserRoleEnvironment, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProjectGroup>(It.IsAny<Expression<Func<ScopedUserRoleProjectGroup, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteMembersByTeamIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteAsync(team, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NoScopedRoles_SkipsScopeChildDeletion()
    {
        var team = new Team { Id = 1 };

        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);
        _scopedUserRoleDataProvider.Setup(p => p.GetByTeamIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ScopedUserRole>());

        await _sut.DeleteAsync(1);

        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProject>(It.IsAny<Expression<Func<ScopedUserRoleProject, bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleEnvironment>(It.IsAny<Expression<Func<ScopedUserRoleEnvironment, bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProjectGroup>(It.IsAny<Expression<Func<ScopedUserRoleProjectGroup, bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteMembersByTeamIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteAsync(team, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NotFound_Throws()
    {
        _teamDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Team)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(99));
    }

    [Fact]
    public async Task Delete_BuiltInTeam_Throws()
    {
        var team = new Team { Id = 1, Name = "Squid Administrators", IsBuiltIn = true };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(1));

        ex.Message.ShouldContain("built-in");
        _teamDataProvider.Verify(p => p.DeleteAsync(It.IsAny<Team>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_BuiltInTeam_Throws()
    {
        var team = new Team { Id = 1, Name = "Squid Administrators", IsBuiltIn = true };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.UpdateAsync(new UpdateTeamCommand { Id = 1, Name = "Hacked" }));

        ex.Message.ShouldContain("built-in");
        _teamDataProvider.Verify(p => p.UpdateAsync(It.IsAny<Team>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddMember_CallsDataProvider()
    {
        var team = new Team { Id = 1, Name = "Dev", IsBuiltIn = false };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        await _sut.AddMemberAsync(1, 42);

        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.TeamId == 1 && m.UserId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveMember_CallsDataProvider()
    {
        var team = new Team { Id = 1, Name = "Dev", IsBuiltIn = false };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        await _sut.RemoveMemberAsync(1, 42);

        _teamDataProvider.Verify(p => p.RemoveMemberAsync(It.Is<TeamMember>(m => m.TeamId == 1 && m.UserId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddMember_EveryoneTeam_Throws()
    {
        var team = new Team { Id = 1, Name = "Everyone", IsBuiltIn = true };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.AddMemberAsync(1, 42));

        ex.Message.ShouldContain("Everyone");
        _teamDataProvider.Verify(p => p.AddMemberAsync(It.IsAny<TeamMember>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveMember_EveryoneTeam_Throws()
    {
        var team = new Team { Id = 1, Name = "Everyone", IsBuiltIn = true };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.RemoveMemberAsync(1, 42));

        ex.Message.ShouldContain("Everyone");
        _teamDataProvider.Verify(p => p.RemoveMemberAsync(It.IsAny<TeamMember>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddMember_AdministratorsTeam_Succeeds()
    {
        var team = new Team { Id = 1, Name = "Squid Administrators", IsBuiltIn = true };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        await _sut.AddMemberAsync(1, 42);

        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.TeamId == 1 && m.UserId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveMember_AdministratorsTeam_Succeeds()
    {
        var team = new Team { Id = 1, Name = "Squid Administrators", IsBuiltIn = true };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        await _sut.RemoveMemberAsync(1, 42);

        _teamDataProvider.Verify(p => p.RemoveMemberAsync(It.Is<TeamMember>(m => m.TeamId == 1 && m.UserId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddMember_NotFound_Throws()
    {
        _teamDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Team)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.AddMemberAsync(99, 42));
    }

    [Fact]
    public async Task RemoveMember_NotFound_Throws()
    {
        _teamDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Team)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.RemoveMemberAsync(99, 42));
    }
}
