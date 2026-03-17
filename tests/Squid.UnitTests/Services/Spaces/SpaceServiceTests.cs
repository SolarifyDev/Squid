using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Spaces;
using Squid.Message.Commands.Spaces;
using Squid.Message.Models.Spaces;

namespace Squid.UnitTests.Services.Spaces;

public class SpaceServiceTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<ISpaceDataProvider> _spaceDataProvider = new();
    private readonly Mock<IRepository> _repository = new();
    private readonly SpaceService _sut;

    public SpaceServiceTests()
    {
        _sut = new SpaceService(_mapper.Object, _spaceDataProvider.Object, _repository.Object);
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

        var result = await _sut.UpdateAsync(command);

        result.ShouldBe(dto);
        _spaceDataProvider.Verify(p => p.UpdateAsync(space, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_ExistingSpace_CallsDataProvider()
    {
        var space = new Space { Id = 2 };
        _spaceDataProvider.Setup(p => p.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(space);

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
    public async Task GetManagerTeams_ReturnsTeamsWithSpaceOwnerRole()
    {
        var spaceOwnerRole = new UserRole { Id = 10, Name = "Space Owner" };
        var scopedRole = new ScopedUserRole { Id = 1, TeamId = 5, UserRoleId = 10, SpaceId = 1 };
        var team = new Team { Id = 5, Name = "Dev Managers" };

        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(new List<ScopedUserRole> { scopedRole }.AsQueryable().BuildMock());
        _repository.Setup(r => r.QueryNoTracking<UserRole>(It.IsAny<Expression<Func<UserRole, bool>>>()))
            .Returns(new List<UserRole> { spaceOwnerRole }.AsQueryable().BuildMock());
        _repository.Setup(r => r.QueryNoTracking<Team>(It.IsAny<Expression<Func<Team, bool>>>()))
            .Returns(new List<Team> { team }.AsQueryable().BuildMock());

        var result = await _sut.GetManagerTeamsAsync(1);

        result.Count.ShouldBe(1);
        result[0].TeamId.ShouldBe(5);
        result[0].TeamName.ShouldBe("Dev Managers");
    }

    [Fact]
    public async Task GetManagerTeams_NoManagers_ReturnsEmpty()
    {
        _repository.Setup(r => r.QueryNoTracking<ScopedUserRole>(It.IsAny<Expression<Func<ScopedUserRole, bool>>>()))
            .Returns(new List<ScopedUserRole>().AsQueryable().BuildMock());
        _repository.Setup(r => r.QueryNoTracking<UserRole>(It.IsAny<Expression<Func<UserRole, bool>>>()))
            .Returns(new List<UserRole>().AsQueryable().BuildMock());
        _repository.Setup(r => r.QueryNoTracking<Team>(It.IsAny<Expression<Func<Team, bool>>>()))
            .Returns(new List<Team>().AsQueryable().BuildMock());

        var result = await _sut.GetManagerTeamsAsync(999);

        result.ShouldBeEmpty();
    }
}
