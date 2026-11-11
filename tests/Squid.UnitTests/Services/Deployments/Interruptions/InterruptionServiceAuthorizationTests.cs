using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Identity;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Interruptions;

public class InterruptionServiceAuthorizationTests
{
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<IInterruptionAuthorizationService> _authService = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly DeploymentInterruptionService _sut;

    public InterruptionServiceAuthorizationTests()
    {
        _sut = new DeploymentInterruptionService(_repository.Object, _unitOfWork.Object, _serverTaskService.Object, _authService.Object, _currentUser.Object);
    }

    // ========== TakeResponsibility Authorization ==========

    [Fact]
    public async Task TakeResponsibility_WithTeams_UserNotInTeam_Throws()
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("User 42 is not a member of any responsible team for this interruption"));

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.TakeResponsibilityAsync(1, "42"));

        _repository.Verify(r => r.UpdateAsync(It.IsAny<DeploymentInterruption>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TakeResponsibility_WithTeams_UserInTeam_Succeeds()
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>())).ReturnsAsync(InterruptionAccess.TeamMember);

        await _sut.TakeResponsibilityAsync(1, "42");

        interruption.ResponsibleUserId.ShouldBe("42");
        _repository.Verify(r => r.UpdateAsync(interruption, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TakeResponsibility_NoTeams_AnyUserSucceeds()
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = null };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);

        await _sut.TakeResponsibilityAsync(1, "99");

        interruption.ResponsibleUserId.ShouldBe("99");
        _authService.Verify(a => a.EnsureCanActAsync(It.IsAny<DeploymentInterruption>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Submit Authorization ==========

    [Fact]
    public async Task Submit_WithTeams_UserNotInTeam_Throws()
    {
        var interruption = new DeploymentInterruption { Id = 1, InterruptionType = InterruptionType.ManualIntervention, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _currentUser.Setup(u => u.Id).Returns(42);
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("User 42 is not a member of any responsible team for this interruption"));

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", "Proceed" } }));

        _repository.Verify(r => r.UpdateAsync(It.IsAny<DeploymentInterruption>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_WithTeams_UserInTeam_Succeeds()
    {
        var interruption = new DeploymentInterruption { Id = 1, ServerTaskId = 10, InterruptionType = InterruptionType.ManualIntervention, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _currentUser.Setup(u => u.Id).Returns(42);
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>())).ReturnsAsync(InterruptionAccess.TeamMember);
        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentInterruption>());

        await _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", "Proceed" } });

        interruption.Resolution.ShouldBe("Proceed");
        _repository.Verify(r => r.UpdateAsync(interruption, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Administrator override audit trail ==========

    [Theory]
    [InlineData(InterruptionAccess.AdministratorOverride, 42, "ops-admin")]
    [InlineData(InterruptionAccess.TeamMember, null, null)]
    [InlineData(InterruptionAccess.Unrestricted, null, null)]
    public async Task Submit_RecordsTheOverridingUserOnlyWhenResolvedViaAdministratorOverride(InterruptionAccess access, int? expectedUserId, string expectedUserName)
    {
        var interruption = new DeploymentInterruption { Id = 1, ServerTaskId = 10, InterruptionType = InterruptionType.ManualIntervention, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _currentUser.Setup(u => u.Id).Returns(42);
        _currentUser.Setup(u => u.Name).Returns("ops-admin");
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>())).ReturnsAsync(access);
        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentInterruption>());

        await _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", "Proceed" } });

        interruption.Resolution.ShouldBe("Proceed");
        interruption.AdministratorOverrideUserId.ShouldBe(expectedUserId);
        interruption.AdministratorOverrideUserName.ShouldBe(expectedUserName);
        _repository.Verify(r => r.UpdateAsync(interruption, It.IsAny<CancellationToken>()), Times.Once);
    }

    // A later submission replaces the resolution, so it must also replace the audit fields: otherwise the
    // resumed deployment would credit the administrator with a decision a team member (or the system) made.
    [Theory]
    [InlineData(InterruptionAccess.TeamMember, 7, "Abort")]
    [InlineData(InterruptionAccess.Unrestricted, 7, "Abort")]
    [InlineData(null, null, "Abort")]
    public async Task Submit_AfterAnAdministratorOverride_ClearsTheOverrideWhenTheNewDecisionIsNotAnOverride(InterruptionAccess? laterAccess, int? laterUserId, string laterResult)
    {
        var interruption = new DeploymentInterruption { Id = 1, ServerTaskId = 10, InterruptionType = InterruptionType.ManualIntervention, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentInterruption>());
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>())).ReturnsAsync(InterruptionAccess.AdministratorOverride);
        _currentUser.Setup(u => u.Id).Returns(42);
        _currentUser.Setup(u => u.Name).Returns("ops-admin");

        await _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", "Proceed" } });

        interruption.AdministratorOverrideUserId.ShouldBe(42);

        if (laterAccess.HasValue)
            _authService.Setup(a => a.EnsureCanActAsync(interruption, laterUserId!.Value, It.IsAny<CancellationToken>())).ReturnsAsync(laterAccess.Value);
        _currentUser.Setup(u => u.Id).Returns(laterUserId);
        _currentUser.Setup(u => u.Name).Returns("team-member");

        await _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", laterResult } });

        interruption.Resolution.ShouldBe(laterResult);
        interruption.AdministratorOverrideUserId.ShouldBeNull();
        interruption.AdministratorOverrideUserName.ShouldBeNull();
    }

    [Fact]
    public async Task Submit_SecondAdministratorOverride_CreditsTheLatestAdministrator()
    {
        var interruption = new DeploymentInterruption { Id = 1, ServerTaskId = 10, InterruptionType = InterruptionType.ManualIntervention, ResponsibleTeamIds = "1", AdministratorOverrideUserId = 42, AdministratorOverrideUserName = "first-admin" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentInterruption>());
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 43, It.IsAny<CancellationToken>())).ReturnsAsync(InterruptionAccess.AdministratorOverride);
        _currentUser.Setup(u => u.Id).Returns(43);
        _currentUser.Setup(u => u.Name).Returns("second-admin");

        await _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", "Abort" } });

        interruption.AdministratorOverrideUserId.ShouldBe(43);
        interruption.AdministratorOverrideUserName.ShouldBe("second-admin");
    }

    [Fact]
    public async Task TakeResponsibility_ViaAdministratorOverride_AssignsButDoesNotMarkTheResolution()
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>())).ReturnsAsync(InterruptionAccess.AdministratorOverride);

        await _sut.TakeResponsibilityAsync(1, "42");

        interruption.ResponsibleUserId.ShouldBe("42");
        interruption.AdministratorOverrideUserId.ShouldBeNull();
        _repository.Verify(r => r.UpdateAsync(interruption, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Edge: null currentUser skips auth ==========

    [Fact]
    public async Task Submit_NullCurrentUser_WithTeams_SkipsAuth()
    {
        var interruption = new DeploymentInterruption { Id = 1, ServerTaskId = 10, InterruptionType = InterruptionType.ManualIntervention, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);
        _currentUser.Setup(u => u.Id).Returns((int?)null);
        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentInterruption>());

        await _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", "Proceed" } });

        _authService.Verify(a => a.EnsureCanActAsync(It.IsAny<DeploymentInterruption>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        interruption.Resolution.ShouldBe("Proceed");
    }

    [Fact]
    public async Task TakeResponsibility_NonNumericUserId_SkipsAuth()
    {
        var interruption = new DeploymentInterruption { Id = 1, ResponsibleTeamIds = "1,2" };

        _repository.Setup(r => r.GetByIdAsync<DeploymentInterruption>(1, It.IsAny<CancellationToken>())).ReturnsAsync(interruption);

        await _sut.TakeResponsibilityAsync(1, "system");

        _authService.Verify(a => a.EnsureCanActAsync(It.IsAny<DeploymentInterruption>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        interruption.ResponsibleUserId.ShouldBe("system");
    }
}
