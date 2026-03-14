using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Interruptions;

public class DeploymentInterruptionServiceTests
{
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly DeploymentInterruptionService _sut;

    public DeploymentInterruptionServiceTests()
    {
        _sut = new DeploymentInterruptionService(_repository.Object, _unitOfWork.Object, _serverTaskService.Object);
    }

    // ========== CancelPendingInterruptionsAsync ==========

    [Fact]
    public async Task CancelPending_BatchCancelsAndClearsFlag()
    {
        var pending = new List<DeploymentInterruption>
        {
            new() { Id = 1, ServerTaskId = 10, Resolution = null },
            new() { Id = 2, ServerTaskId = 10, Resolution = null }
        };

        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        await _sut.CancelPendingInterruptionsAsync(10);

        pending[0].Resolution.ShouldBe("Abort");
        pending[1].Resolution.ShouldBe("Abort");
        pending[0].ResolvedAt.ShouldNotBeNull();
        pending[1].ResolvedAt.ShouldNotBeNull();

        _repository.Verify(r => r.UpdateAsync(It.IsAny<DeploymentInterruption>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _serverTaskService.Verify(s => s.SetHasPendingInterruptionsAsync(10, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelPending_NoPending_DoesNotSaveOrClearFlag()
    {
        _repository.Setup(r => r.ToListAsync<DeploymentInterruption>(It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentInterruption, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentInterruption>());

        await _sut.CancelPendingInterruptionsAsync(10);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _serverTaskService.Verify(s => s.SetHasPendingInterruptionsAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
