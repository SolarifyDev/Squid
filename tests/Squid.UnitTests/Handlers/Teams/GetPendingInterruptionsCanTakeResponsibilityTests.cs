using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Handlers.RequestHandlers.Deployments.Interruption;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.Message.Enums.Deployments;
using Squid.Message.Requests.Deployments.Interruption;

namespace Squid.UnitTests.Handlers.Teams;

public class GetPendingInterruptionsCanTakeResponsibilityTests
{
    private readonly Mock<IDeploymentInterruptionService> _interruptionService = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    private GetPendingInterruptionsRequestHandler CreateHandler()
        => new(_interruptionService.Object, _teamDataProvider.Object, _currentUser.Object);

    [Fact]
    public async Task NoTeams_CanTakeResponsibility_IsTrue()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = null });
        _currentUser.Setup(u => u.Id).Returns(42);
        _teamDataProvider.Setup(p => p.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeTrue();
    }

    [Fact]
    public async Task UserInTeam_CanTakeResponsibility_IsTrue()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "1,2" });
        _currentUser.Setup(u => u.Id).Returns(42);
        _teamDataProvider.Setup(p => p.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 2, 5 });

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeTrue();
    }

    [Fact]
    public async Task UserNotInTeam_CanTakeResponsibility_IsFalse()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "1,2" });
        _currentUser.Setup(u => u.Id).Returns(42);
        _teamDataProvider.Setup(p => p.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 5, 6 });

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeFalse();
    }

    [Fact]
    public async Task NoCurrentUser_WithTeams_CanTakeResponsibility_IsFalse()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "1,2" });
        _currentUser.Setup(u => u.Id).Returns((int?)null);

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeFalse();
        _teamDataProvider.Verify(p => p.GetTeamIdsByUserIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoCurrentUser_NoTeams_CanTakeResponsibility_IsTrue()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = null });
        _currentUser.Setup(u => u.Id).Returns((int?)null);

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeTrue();
    }

    [Fact]
    public async Task InvalidCsv_CanTakeResponsibility_IsTrue()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "abc,," });
        _currentUser.Setup(u => u.Id).Returns(42);
        _teamDataProvider.Setup(p => p.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeTrue();
    }

    [Fact]
    public async Task ResponsibleTeamIds_PassedThrough()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "3,7" });
        _currentUser.Setup(u => u.Id).Returns(42);
        _teamDataProvider.Setup(p => p.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 3 });

        var result = await HandleRequest(10);

        result.Interruptions.Single().ResponsibleTeamIds.ShouldBe("3,7");
    }

    [Fact]
    public async Task MultipleInterruptions_SingleDbCall_ForUserTeams()
    {
        SetupInterruptions(
            new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "1" },
            new DeploymentInterruption { Id = 2, ServerTaskId = 10, ResponsibleTeamIds = "2" },
            new DeploymentInterruption { Id = 3, ServerTaskId = 10, ResponsibleTeamIds = null });
        _currentUser.Setup(u => u.Id).Returns(42);
        _teamDataProvider.Setup(p => p.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 1 });

        var result = await HandleRequest(10);

        result.Interruptions.Count.ShouldBe(3);
        result.Interruptions[0].CanTakeResponsibility.ShouldBeTrue();
        result.Interruptions[1].CanTakeResponsibility.ShouldBeFalse();
        result.Interruptions[2].CanTakeResponsibility.ShouldBeTrue();

        _teamDataProvider.Verify(p => p.GetTeamIdsByUserIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void SetupInterruptions(params DeploymentInterruption[] interruptions)
    {
        _interruptionService.Setup(s => s.GetPendingInterruptionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(interruptions.ToList());
    }

    private async Task<GetPendingInterruptionsResponse> HandleRequest(int serverTaskId)
    {
        var handler = CreateHandler();
        var context = new Mock<IReceiveContext<GetPendingInterruptionsRequest>>();
        context.Setup(c => c.Message).Returns(new GetPendingInterruptionsRequest { ServerTaskId = serverTaskId });

        return await handler.Handle(context.Object, CancellationToken.None);
    }
}
