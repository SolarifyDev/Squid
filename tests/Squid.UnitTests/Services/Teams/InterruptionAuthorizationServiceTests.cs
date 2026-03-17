using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Teams;

namespace Squid.UnitTests.Services.Teams;

public class InterruptionAuthorizationServiceTests
{
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly InterruptionAuthorizationService _sut;

    public InterruptionAuthorizationServiceTests()
    {
        _sut = new InterruptionAuthorizationService(_teamDataProvider.Object);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoTeams_Allows(string responsibleTeamIds)
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = responsibleTeamIds };

        await _sut.EnsureCanActAsync(interruption, 42);

        _teamDataProvider.Verify(p => p.IsUserInAnyTeamAsync(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UserInTeam_Allows()
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = "1,2" };

        _teamDataProvider.Setup(p => p.IsUserInAnyTeamAsync(42, It.Is<List<int>>(ids => ids.Contains(1) && ids.Contains(2)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.EnsureCanActAsync(interruption, 42);

        _teamDataProvider.Verify(p => p.IsUserInAnyTeamAsync(42, It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UserNotInTeam_Throws()
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = "1,2" };

        _teamDataProvider.Setup(p => p.IsUserInAnyTeamAsync(42, It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.EnsureCanActAsync(interruption, 42));
    }

    [Theory]
    [InlineData("abc,,")]
    [InlineData(",,,")]
    [InlineData("0,0")]
    public async Task InvalidCsv_Allows(string responsibleTeamIds)
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = responsibleTeamIds };

        await _sut.EnsureCanActAsync(interruption, 42);

        _teamDataProvider.Verify(p => p.IsUserInAnyTeamAsync(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
