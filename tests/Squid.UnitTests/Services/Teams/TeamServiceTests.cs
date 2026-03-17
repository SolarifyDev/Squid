using System;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Teams;
using Squid.Message.Models.Teams;

namespace Squid.UnitTests.Services.Teams;

public class TeamServiceTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly TeamService _sut;

    public TeamServiceTests()
    {
        _sut = new TeamService(_mapper.Object, _teamDataProvider.Object);
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
    public async Task Delete_ExistingTeam_CallsDataProvider()
    {
        var team = new Team { Id = 1 };
        _teamDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(team);

        await _sut.DeleteAsync(1);

        _teamDataProvider.Verify(p => p.DeleteAsync(team, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NotFound_Throws()
    {
        _teamDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Team)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(99));
    }

    [Fact]
    public async Task AddMember_CallsDataProvider()
    {
        await _sut.AddMemberAsync(1, 42);

        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.TeamId == 1 && m.UserId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveMember_CallsDataProvider()
    {
        await _sut.RemoveMemberAsync(1, 42);

        _teamDataProvider.Verify(p => p.RemoveMemberAsync(It.Is<TeamMember>(m => m.TeamId == 1 && m.UserId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
