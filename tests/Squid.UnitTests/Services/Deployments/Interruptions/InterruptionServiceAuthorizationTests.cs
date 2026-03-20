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
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

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
        _authService.Setup(a => a.EnsureCanActAsync(interruption, 42, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentInterruption>());

        await _sut.SubmitInterruptionAsync(1, new Dictionary<string, string> { { "Result", "Proceed" } });

        interruption.Resolution.ShouldBe("Proceed");
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
