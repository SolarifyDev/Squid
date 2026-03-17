using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authentication;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Account;

namespace Squid.UnitTests.Services.Account;

public class AccountServiceTests
{
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IUserTokenService> _tokenService = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly AccountService _sut;

    public AccountServiceTests()
    {
        _sut = new AccountService(_repository.Object, _unitOfWork.Object, _tokenService.Object, _teamDataProvider.Object);
    }

    [Fact]
    public async Task CreateUser_AddsUserToEveryoneTeam()
    {
        _repository.Setup(r => r.AnyAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var everyoneTeam = new Team { Id = 10, Name = "Everyone", SpaceId = 0 };
        _teamDataProvider.Setup(p => p.GetAllBySpaceAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { everyoneTeam });

        await _sut.CreateUserAsync(new CreateUserCommand { UserName = "testuser", Password = "Test@123456" });

        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.TeamId == 10), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateUser_NoEveryoneTeam_DoesNotThrow()
    {
        _repository.Setup(r => r.AnyAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        _teamDataProvider.Setup(p => p.GetAllBySpaceAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team>());

        var result = await _sut.CreateUserAsync(new CreateUserCommand { UserName = "testuser", Password = "Test@123456" });

        result.IsSucceeded.ShouldBeTrue();
        _teamDataProvider.Verify(p => p.AddMemberAsync(It.IsAny<TeamMember>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }
}
