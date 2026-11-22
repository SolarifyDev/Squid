using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// The lease starts here, after the claim: deleting <c>leaseKeeper.Start</c> from <c>LoadTaskPhase</c> fails the first test, and starting it
/// before the claim (so a fenced duplicate would renew a lease it does not hold) fails the second.
/// </summary>
public class LoadTaskPhaseLeaseTests
{
    private readonly Mock<IServerTaskService> _service = new();
    private readonly Mock<IServerTaskLeaseKeeper> _keeper = new();
    private readonly Mock<IServerTaskLease> _lease = new();

    [Fact]
    public async Task SuccessfulClaim_StartsTheLeaseForThatTask_AndHandsItToTheContext()
    {
        _service.Setup(s => s.StartExecutingAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new StartExecutingResult(new ServerTaskEntity { Id = 42 }, false));
        _keeper.Setup(k => k.Start(42)).Returns(_lease.Object);
        var ctx = new DeploymentTaskContext { ServerTaskId = 42 };

        await new LoadTaskPhase(_service.Object, _keeper.Object).ExecuteAsync(ctx, CancellationToken.None);

        _keeper.Verify(k => k.Start(42), Times.Once);
        ctx.Lease.ShouldBeSameAs(_lease.Object);
    }

    [Fact]
    public async Task ClaimRefusedByALiveOwner_StartsNoLease()
    {
        _service.Setup(s => s.StartExecutingAsync(42, It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskOwnedByLiveProcessException(42, "pod-a:1:x"));
        var ctx = new DeploymentTaskContext { ServerTaskId = 42 };

        await Should.ThrowAsync<ServerTaskOwnedByLiveProcessException>(() => new LoadTaskPhase(_service.Object, _keeper.Object).ExecuteAsync(ctx, CancellationToken.None));

        _keeper.Verify(k => k.Start(It.IsAny<int>()), Times.Never);
        ctx.Lease.ShouldBeNull();
    }
}
