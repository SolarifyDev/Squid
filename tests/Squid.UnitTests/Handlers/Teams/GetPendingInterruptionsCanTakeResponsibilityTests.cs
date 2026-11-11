using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Handlers.RequestHandlers.Deployments.Interruption;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Deployments;
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
    private readonly Mock<IAuthorizationService> _authorization = new();

    public GetPendingInterruptionsCanTakeResponsibilityTests()
    {
        _authorization.Setup(a => a.CheckPermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(PermissionCheckResult.Denied("no override"));
    }

    // The handler asks the SAME authorization service submit / take-responsibility use, so what the UI
    // offers can never disagree with what the server accepts.
    private GetPendingInterruptionsRequestHandler CreateHandler()
        => new(_interruptionService.Object, new InterruptionAuthorizationService(_teamDataProvider.Object, _authorization.Object, new Mock<IDeploymentDataProvider>().Object), _currentUser.Object);

    private void SetupUserTeams(params int[] teamIds)
        => _teamDataProvider.Setup(p => p.IsUserInAnyTeamAsync(42, It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, List<int> required, CancellationToken _) => required.Intersect(teamIds).Any());

    [Fact]
    public async Task NoTeams_CanTakeResponsibility_IsTrue()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = null });
        _currentUser.Setup(u => u.Id).Returns(42);
        SetupUserTeams();

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeTrue();
    }

    [Fact]
    public async Task UserInTeam_CanTakeResponsibility_IsTrue()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "1,2" });
        _currentUser.Setup(u => u.Id).Returns(42);
        SetupUserTeams(2, 5);

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeTrue();
    }

    [Fact]
    public async Task UserNotInTeam_CanTakeResponsibility_IsFalse()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "1,2" });
        _currentUser.Setup(u => u.Id).Returns(42);
        SetupUserTeams(5, 6);

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
        _teamDataProvider.Verify(p => p.IsUserInAnyTeamAsync(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
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
        SetupUserTeams();

        var result = await HandleRequest(10);

        result.Interruptions.Single().CanTakeResponsibility.ShouldBeTrue();
    }

    [Fact]
    public async Task ResponsibleTeamIds_PassedThrough()
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "3,7" });
        _currentUser.Setup(u => u.Id).Returns(42);
        SetupUserTeams(3);

        var result = await HandleRequest(10);

        result.Interruptions.Single().ResponsibleTeamIds.ShouldBe("3,7");
    }

    [Fact]
    public async Task MultipleInterruptions_EachDecidedIndependently_AndUnrestrictedOnesNeverHitTheDatabase()
    {
        SetupInterruptions(
            new DeploymentInterruption { Id = 1, ServerTaskId = 10, ResponsibleTeamIds = "1" },
            new DeploymentInterruption { Id = 2, ServerTaskId = 10, ResponsibleTeamIds = "2" },
            new DeploymentInterruption { Id = 3, ServerTaskId = 10, ResponsibleTeamIds = null });
        _currentUser.Setup(u => u.Id).Returns(42);
        SetupUserTeams(1);

        var result = await HandleRequest(10);

        result.Interruptions.Count.ShouldBe(3);
        result.Interruptions[0].CanTakeResponsibility.ShouldBeTrue();
        result.Interruptions[1].CanTakeResponsibility.ShouldBeFalse();
        result.Interruptions[2].CanTakeResponsibility.ShouldBeTrue();
        result.Interruptions.ShouldAllBe(i => !i.IsAdministratorOverride);

        _teamDataProvider.Verify(p => p.IsUserInAnyTeamAsync(42, It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    public async Task OverridePermission_LetsNonMembersAct_AndIsFlaggedOnlyWhenItIsTheReason(bool holdsOverride, bool memberOfTeam, bool expectedCanAct, bool expectedOverride)
    {
        SetupInterruptions(new DeploymentInterruption { Id = 1, ServerTaskId = 10, SpaceId = 1, ResponsibleTeamIds = "1" });
        _currentUser.Setup(u => u.Id).Returns(42);
        SetupUserTeams(memberOfTeam ? [1] : [9]);
        _authorization.Setup(a => a.CheckPermissionAsync(It.Is<PermissionCheckRequest>(r => r.UserId == 42 && r.SpaceId == 1 && r.Permission == Squid.Message.Enums.Permission.InterruptionSubmitOverride), It.IsAny<CancellationToken>()))
            .ReturnsAsync(holdsOverride ? PermissionCheckResult.Authorized() : PermissionCheckResult.Denied("no"));

        var dto = (await HandleRequest(10)).Interruptions.Single();

        dto.CanTakeResponsibility.ShouldBe(expectedCanAct);
        dto.IsAdministratorOverride.ShouldBe(expectedOverride);
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
