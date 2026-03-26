using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Spaces;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Spaces;
using Squid.Message.Models.Spaces;

namespace Squid.UnitTests.Services.Spaces;

public class SpaceServiceTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<ISpaceDataProvider> _spaceDataProvider = new();
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly Mock<IScopedUserRoleDataProvider> _scopedUserRoleDataProvider = new();
    private readonly Mock<IUserRoleDataProvider> _userRoleDataProvider = new();
    private readonly SpaceService _sut;

    private readonly UserRole _spaceOwnerRole = new() { Id = 10, Name = "Space Owner", IsBuiltIn = true };

    public SpaceServiceTests()
    {
        _sut = new SpaceService(_mapper.Object, _spaceDataProvider.Object, _repository.Object, _teamDataProvider.Object, _scopedUserRoleDataProvider.Object, _userRoleDataProvider.Object);
        _userRoleDataProvider.Setup(p => p.GetByNameAsync("Space Owner", It.IsAny<CancellationToken>())).ReturnsAsync(_spaceOwnerRole);
    }

    [Fact]
    public async Task Create_MapsAndCallsDataProvider()
    {
        var command = new CreateSpaceCommand { Name = "Dev", Slug = "dev", Description = "Dev space" };
        var space = new Space { Id = 1, Name = "Dev" };
        var dto = new SpaceDto { Id = 1, Name = "Dev" };

        _mapper.Setup(m => m.Map<Space>(command)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);

        var result = await _sut.CreateAsync(command);

        result.ShouldBe(dto);
        _spaceDataProvider.Verify(p => p.AddAsync(space, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_ExistingSpace_MapsAndUpdates()
    {
        var command = new UpdateSpaceCommand { Id = 1, Name = "Dev v2", Slug = "dev-v2" };
        var space = new Space { Id = 1, Name = "Dev" };
        var dto = new SpaceDto { Id = 1, Name = "Dev v2" };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _mapper.Setup(m => m.Map(command, space)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);
        SetupEmptyScopedRoleQuery();

        var result = await _sut.UpdateAsync(command);

        result.ShouldBe(dto);
        _spaceDataProvider.Verify(p => p.UpdateAsync(space, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        _spaceDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Space)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.UpdateAsync(new UpdateSpaceCommand { Id = 99 }));
    }

    [Fact]
    public async Task Update_DefaultSpace_AllowsUpdate()
    {
        var command = new UpdateSpaceCommand { Id = 1, Name = "Default Updated", Slug = "default" };
        var space = new Space { Id = 1, Name = "Default", IsDefault = true };
        var dto = new SpaceDto { Id = 1, Name = "Default Updated", IsDefault = true };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _mapper.Setup(m => m.Map(command, space)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);
        SetupEmptyScopedRoleQuery();

        var result = await _sut.UpdateAsync(command);

        result.ShouldBe(dto);
        _spaceDataProvider.Verify(p => p.UpdateAsync(space, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_ExistingSpace_CallsDataProvider()
    {
        var space = new Space { Id = 2 };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        SetupScopedRoleIdsQuery(new List<ScopedUserRole>());
        SetupSpaceScopedTeamsQuery(new List<Team>());

        await _sut.DeleteAsync(2);

        _spaceDataProvider.Verify(p => p.DeleteAsync(space, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NotFound_Throws()
    {
        _spaceDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Space)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(99));
    }

    [Fact]
    public async Task Delete_DefaultSpace_Throws()
    {
        var space = new Space { Id = 1, Name = "Default", IsDefault = true };
        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(1));

        ex.Message.ShouldContain("default");
        _spaceDataProvider.Verify(p => p.DeleteAsync(It.IsAny<Space>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetById_NotFound_Throws()
    {
        _spaceDataProvider.Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Space)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.GetByIdAsync(99));
    }

    [Fact]
    public async Task GetManagers_ReturnsTeamsAndUsers()
    {
        var space = new Space { Id = 1, OwnerTeamId = 100 };
        var scopedRoles = new List<ScopedUserRole>
        {
            new() { Id = 1, TeamId = 5, UserRoleId = 10, SpaceId = 1 },
            new() { Id = 2, TeamId = 100, UserRoleId = 10, SpaceId = 1 },
        };
        var teams = new List<Team>
        {
            new() { Id = 5, Name = "Dev Managers" },
            new() { Id = 100, Name = "Space Owners (Prod)" },
        };
        var members = new List<TeamMember>
        {
            new() { TeamId = 100, UserId = 1 },
            new() { TeamId = 100, UserId = 2 },
        };
        var users = new List<UserAccount>
        {
            new() { Id = 1, UserName = "admin", DisplayName = "Administrator" },
            new() { Id = 2, UserName = "bob", DisplayName = "Bob Smith" },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(scopedRoles.AsQueryable().BuildMock());
        _repository.Setup(r => r.QueryNoTracking<Team>(It.IsAny<Expression<Func<Team, bool>>>()))
            .Returns(teams.AsQueryable().BuildMock());
        _repository.Setup(r => r.QueryNoTracking<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>()))
            .Returns(users.AsQueryable().BuildMock());
        _teamDataProvider.Setup(p => p.GetMembersByTeamIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(members);

        var result = await _sut.GetManagersAsync(1);

        result.Teams.Count.ShouldBe(1);
        result.Teams[0].TeamId.ShouldBe(5);
        result.Teams[0].TeamName.ShouldBe("Dev Managers");
        result.Users.Count.ShouldBe(2);
        result.Users.ShouldContain(u => u.UserId == 1 && u.UserName == "admin");
        result.Users.ShouldContain(u => u.UserId == 2 && u.UserName == "bob");
    }

    [Fact]
    public async Task GetManagers_NoUsers_ReturnsEmptyUsersList()
    {
        var space = new Space { Id = 1, OwnerTeamId = null };
        var scopedRoles = new List<ScopedUserRole>
        {
            new() { Id = 1, TeamId = 5, UserRoleId = 10, SpaceId = 1 },
        };
        var teams = new List<Team>
        {
            new() { Id = 5, Name = "Dev Managers" },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(scopedRoles.AsQueryable().BuildMock());
        _repository.Setup(r => r.QueryNoTracking<Team>(It.IsAny<Expression<Func<Team, bool>>>()))
            .Returns(teams.AsQueryable().BuildMock());

        var result = await _sut.GetManagersAsync(1);

        result.Teams.Count.ShouldBe(1);
        result.Users.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WithOwnerTeams_AssignsSpaceOwnerRole()
    {
        var command = new CreateSpaceCommand { Name = "Prod", Slug = "prod", OwnerTeamIds = new List<int> { 3, 5 } };
        var space = new Space { Id = 1, Name = "Prod" };
        var dto = new SpaceDto { Id = 1, Name = "Prod" };

        _mapper.Setup(m => m.Map<Space>(command)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);

        await _sut.CreateAsync(command);

        _scopedUserRoleDataProvider.Verify(p => p.AddAsync(It.Is<ScopedUserRole>(sr => sr.TeamId == 3 && sr.UserRoleId == 10 && sr.SpaceId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(p => p.AddAsync(It.Is<ScopedUserRole>(sr => sr.TeamId == 5 && sr.UserRoleId == 10 && sr.SpaceId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WithOwnerUsers_CreatesAutoTeamAndAssigns()
    {
        var command = new CreateSpaceCommand { Name = "Prod", Slug = "prod", OwnerUserIds = new List<int> { 1, 2 } };
        var space = new Space { Id = 1, Name = "Prod" };
        var dto = new SpaceDto { Id = 1, Name = "Prod" };

        _mapper.Setup(m => m.Map<Space>(command)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);

        await _sut.CreateAsync(command);

        _teamDataProvider.Verify(p => p.AddAsync(It.Is<Team>(t => t.Name == "Space Owners (Prod)" && t.SpaceId == 0 && t.IsBuiltIn), true, It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(p => p.AddAsync(It.Is<ScopedUserRole>(sr => sr.UserRoleId == 10 && sr.SpaceId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.UserId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.UserId == 2), true, It.IsAny<CancellationToken>()), Times.Once);
        _spaceDataProvider.Verify(p => p.UpdateAsync(It.Is<Space>(s => s.OwnerTeamId != null), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WithBothTeamsAndUsers_HandlesAll()
    {
        var command = new CreateSpaceCommand { Name = "Prod", Slug = "prod", OwnerTeamIds = new List<int> { 3 }, OwnerUserIds = new List<int> { 1 } };
        var space = new Space { Id = 1, Name = "Prod" };
        var dto = new SpaceDto { Id = 1, Name = "Prod" };

        _mapper.Setup(m => m.Map<Space>(command)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);

        await _sut.CreateAsync(command);

        _scopedUserRoleDataProvider.Verify(p => p.AddAsync(It.Is<ScopedUserRole>(sr => sr.TeamId == 3 && sr.SpaceId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.AddAsync(It.Is<Team>(t => t.Name == "Space Owners (Prod)" && t.IsBuiltIn), true, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.UserId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_SyncOwnerTeams_AddsAndRemoves()
    {
        var command = new UpdateSpaceCommand { Id = 1, Name = "Prod", Slug = "prod", OwnerTeamIds = new List<int> { 5, 7 } };
        var space = new Space { Id = 1, Name = "Prod" };
        var dto = new SpaceDto { Id = 1, Name = "Prod" };

        var existingScopedRoles = new List<ScopedUserRole>
        {
            new() { Id = 100, TeamId = 3, UserRoleId = 10, SpaceId = 1 },
            new() { Id = 101, TeamId = 5, UserRoleId = 10, SpaceId = 1 },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _mapper.Setup(m => m.Map(command, space)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(existingScopedRoles.AsQueryable().BuildMock());

        await _sut.UpdateAsync(command);

        _scopedUserRoleDataProvider.Verify(p => p.DeleteAsync(100, It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(p => p.AddAsync(It.Is<ScopedUserRole>(sr => sr.TeamId == 7 && sr.SpaceId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(p => p.DeleteAsync(101, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_SyncOwnerUsers_AddsAndRemovesMembers()
    {
        var command = new UpdateSpaceCommand { Id = 1, Name = "Prod", Slug = "prod", OwnerUserIds = new List<int> { 2, 3 } };
        var space = new Space { Id = 1, Name = "Prod", OwnerTeamId = 100 };
        var dto = new SpaceDto { Id = 1, Name = "Prod" };

        var existingMembers = new List<TeamMember>
        {
            new() { TeamId = 100, UserId = 1 },
            new() { TeamId = 100, UserId = 2 },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _mapper.Setup(m => m.Map(command, space)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);
        SetupEmptyScopedRoleQuery();
        _teamDataProvider.Setup(p => p.GetMembersByTeamIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(existingMembers);

        await _sut.UpdateAsync(command);

        _teamDataProvider.Verify(p => p.RemoveMemberAsync(It.Is<TeamMember>(m => m.UserId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.UserId == 3 && m.TeamId == 100), true, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.RemoveMemberAsync(It.Is<TeamMember>(m => m.UserId == 2), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_RemoveAllUsers_DeletesAutoTeam()
    {
        var command = new UpdateSpaceCommand { Id = 1, Name = "Prod", Slug = "prod", OwnerUserIds = new List<int>() };
        var space = new Space { Id = 1, Name = "Prod", OwnerTeamId = 100 };
        var dto = new SpaceDto { Id = 1, Name = "Prod" };
        var autoTeam = new Team { Id = 100, Name = "Space Owners (Prod)", IsBuiltIn = true };

        var existingMembers = new List<TeamMember>
        {
            new() { TeamId = 100, UserId = 1 },
        };

        var autoTeamScopedRoles = new List<ScopedUserRole>
        {
            new() { Id = 200, TeamId = 100, UserRoleId = 10, SpaceId = 1 },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _mapper.Setup(m => m.Map(command, space)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);
        SetupEmptyScopedRoleQuery();
        _teamDataProvider.Setup(p => p.GetMembersByTeamIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(existingMembers);
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(autoTeamScopedRoles.AsQueryable().BuildMock());
        _teamDataProvider.Setup(p => p.GetByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(autoTeam);

        await _sut.UpdateAsync(command);

        _teamDataProvider.Verify(p => p.RemoveMemberAsync(It.Is<TeamMember>(m => m.UserId == 1), true, It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(p => p.DeleteAsync(200, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteAsync(autoTeam, true, It.IsAny<CancellationToken>()), Times.Once);
        space.OwnerTeamId.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_CleansUpScopedRolesAndAutoTeam()
    {
        var autoTeam = new Team { Id = 100, Name = "Space Owners (Prod)", IsBuiltIn = true };
        var space = new Space { Id = 2, OwnerTeamId = 100 };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        SetupScopedRoleIdsQuery(new List<ScopedUserRole>());
        SetupSpaceScopedTeamsQuery(new List<Team>());
        _teamDataProvider.Setup(p => p.GetByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(autoTeam);

        await _sut.DeleteAsync(2);

        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteMembersByTeamIdAsync(100, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteAsync(autoTeam, true, It.IsAny<CancellationToken>()), Times.Once);
        _spaceDataProvider.Verify(p => p.DeleteAsync(space, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_CleansUpScopeRestrictions()
    {
        var space = new Space { Id = 2 };
        var scopedRoles = new List<ScopedUserRole>
        {
            new() { Id = 10, TeamId = 3, UserRoleId = 5, SpaceId = 2 },
            new() { Id = 11, TeamId = 4, UserRoleId = 6, SpaceId = 2 },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        SetupScopedRoleIdsQuery(scopedRoles);
        SetupSpaceScopedTeamsQuery(new List<Team>());

        await _sut.DeleteAsync(2);

        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProject>(It.IsAny<Expression<Func<ScopedUserRoleProject, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleEnvironment>(It.IsAny<Expression<Func<ScopedUserRoleEnvironment, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProjectGroup>(It.IsAny<Expression<Func<ScopedUserRoleProjectGroup, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_CleansUpSpaceScopedTeams()
    {
        var space = new Space { Id = 2 };
        var spaceScopedTeams = new List<Team>
        {
            new() { Id = 50, Name = "Team A", SpaceId = 2 },
            new() { Id = 51, Name = "Team B", SpaceId = 2 },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        SetupScopedRoleIdsQuery(new List<ScopedUserRole>());
        SetupSpaceScopedTeamsQuery(spaceScopedTeams);

        await _sut.DeleteAsync(2);

        _teamDataProvider.Verify(p => p.DeleteMembersByTeamIdAsync(50, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteMembersByTeamIdAsync(51, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteAsync(It.Is<Team>(t => t.Id == 50), true, It.IsAny<CancellationToken>()), Times.Once);
        _teamDataProvider.Verify(p => p.DeleteAsync(It.Is<Team>(t => t.Id == 51), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_DeleteAutoTeam_DeletesScopeChildrenFirst()
    {
        var command = new UpdateSpaceCommand { Id = 1, Name = "Prod", Slug = "prod", OwnerUserIds = new List<int>() };
        var space = new Space { Id = 1, Name = "Prod", OwnerTeamId = 100 };
        var dto = new SpaceDto { Id = 1, Name = "Prod" };
        var autoTeam = new Team { Id = 100, Name = "Space Owners (Prod)", IsBuiltIn = true };

        var existingMembers = new List<TeamMember>
        {
            new() { TeamId = 100, UserId = 1 },
        };

        var autoTeamScopedRoles = new List<ScopedUserRole>
        {
            new() { Id = 200, TeamId = 100, UserRoleId = 10, SpaceId = 1 },
        };

        _spaceDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(space);
        _mapper.Setup(m => m.Map(command, space)).Returns(space);
        _mapper.Setup(m => m.Map<SpaceDto>(space)).Returns(dto);
        SetupEmptyScopedRoleQuery();
        _teamDataProvider.Setup(p => p.GetMembersByTeamIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(existingMembers);
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(autoTeamScopedRoles.AsQueryable().BuildMock());
        _teamDataProvider.Setup(p => p.GetByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(autoTeam);

        await _sut.UpdateAsync(command);

        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProject>(It.IsAny<Expression<Func<ScopedUserRoleProject, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleEnvironment>(It.IsAny<Expression<Func<ScopedUserRoleEnvironment, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ExecuteDeleteAsync<ScopedUserRoleProjectGroup>(It.IsAny<Expression<Func<ScopedUserRoleProjectGroup, bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _scopedUserRoleDataProvider.Verify(p => p.DeleteAsync(200, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void SetupScopedRoleIdsQuery(List<ScopedUserRole> scopedRoles)
    {
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(scopedRoles.AsQueryable().BuildMock());
    }

    private void SetupSpaceScopedTeamsQuery(List<Team> teams)
    {
        _repository.Setup(r => r.QueryNoTracking<Team>(It.IsAny<Expression<Func<Team, bool>>>()))
            .Returns(teams.AsQueryable().BuildMock());
    }

    private void SetupEmptyScopedRoleQuery()
    {
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(new List<ScopedUserRole>().AsQueryable().BuildMock());
    }
}
