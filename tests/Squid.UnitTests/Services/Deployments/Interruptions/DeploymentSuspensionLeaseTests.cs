using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;

namespace Squid.UnitTests.Services.Deployments.Interruptions;

/// <summary>
/// The suspend stops lease renewal before it writes the pause (so the keeper cannot read the pause as a loss). If that write then fails, the run no
/// longer knows whether it still owns the task and nothing renews the lease: it must fence itself (lease marked lost, so the run stops) rather than
/// carry on until the reaper pauses the task and an auto-resume starts a second run beside it.
/// </summary>
public class DeploymentSuspensionLeaseTests
{
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<IServerTaskLease> _lease = new();

    [Fact]
    public async Task WhenThePauseWriteSucceeds_ReleasesTheLease_AndDoesNotFence()
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = 7, Lease = _lease.Object };

        var suspended = await DeploymentSuspension.PauseAsync(_serverTaskService.Object, 7, ctx, CancellationToken.None);

        _lease.Verify(l => l.Release(), Times.Once);
        _lease.Verify(l => l.Fence(), Times.Never);
        ctx.Suspended.ShouldBeSameAs(suspended);
    }

    [Fact]
    public async Task WhenThePauseWriteThrows_FencesTheLease_RethrowsTheFailure_AndSuspendsNothing()
    {
        var boom = new InvalidOperationException("db down");
        _serverTaskService.Setup(s => s.TransitionStateAsync(7, TaskState.Executing, TaskState.Paused, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(boom);
        var ctx = new DeploymentTaskContext { ServerTaskId = 7, Lease = _lease.Object };

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => DeploymentSuspension.PauseAsync(_serverTaskService.Object, 7, ctx, CancellationToken.None));

        thrown.ShouldBeSameAs(boom);
        _lease.Verify(l => l.Fence(), Times.Once);
        ctx.Suspended.ShouldBeNull();
        ctx.SuspendToken.IsCancellationRequested.ShouldBeFalse();
    }

    // A cancellation (user cancel, shutdown, timeout) already ends the run through its own handler, which still needs the lease to record the outcome.
    [Fact]
    public async Task WhenThePauseWriteIsCancelled_DoesNotFence()
    {
        _serverTaskService.Setup(s => s.TransitionStateAsync(7, TaskState.Executing, TaskState.Paused, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var ctx = new DeploymentTaskContext { ServerTaskId = 7, Lease = _lease.Object };

        await Should.ThrowAsync<OperationCanceledException>(() => DeploymentSuspension.PauseAsync(_serverTaskService.Object, 7, ctx, CancellationToken.None));

        _lease.Verify(l => l.Fence(), Times.Never);
    }

    // The row says Cancelling: the user's cancel owns the transition and the run ends through the cancel handler, so fencing would only delay it to the reaper.
    [Theory]
    [InlineData(TaskState.Cancelling, false)]
    [InlineData(TaskState.Paused, true)]
    [InlineData(TaskState.Failed, true)]
    public async Task WhenThePauseWriteIsRefusedByTheRowsState_FencesUnlessACancelIsPending(string actualState, bool fenced)
    {
        _serverTaskService.Setup(s => s.TransitionStateAsync(7, TaskState.Executing, TaskState.Paused, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskStateTransitionException(actualState, TaskState.Paused));
        var ctx = new DeploymentTaskContext { ServerTaskId = 7, Lease = _lease.Object };

        await Should.ThrowAsync<ServerTaskStateTransitionException>(() => DeploymentSuspension.PauseAsync(_serverTaskService.Object, 7, ctx, CancellationToken.None));

        _lease.Verify(l => l.Fence(), fenced ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task WithoutARunContext_StillPausesUnconditionally()
    {
        await DeploymentSuspension.PauseAsync(_serverTaskService.Object, 7, null, CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(7, TaskState.Executing, TaskState.Paused, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
