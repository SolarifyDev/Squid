using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask;

/// <summary>
/// A delivery that reads the task as Pending/Paused but loses the claim CAS finds the row Executing (the winner's). It must take the fenced
/// path, not surface the transition error, which the runner would turn into Executing to Failed on the winner's live deployment.
/// </summary>
public class ServerTaskServiceClaimFenceTests
{
    private readonly Mock<IServerTaskDataProvider> _tasks = new();
    private readonly Mock<IServerTaskLeaseDataProvider> _leases = new();

    [Theory]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Paused)]
    public async Task LostClaim_ToALiveWinner_IsFenced_NamingTheWinner(string from)
    {
        _tasks.SetupSequence(t => t.GetServerTaskByIdNoTrackingAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 7, State = from }).ReturnsAsync(new ServerTaskEntity { Id = 7, State = TaskState.Executing, LeaseOwner = "winner:1:x" });
        _tasks.Setup(t => t.TransitionStateAsync(7, from, TaskState.Executing, ServerTaskLeaseOwner.Current, It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskStateTransitionException(TaskState.Executing, TaskState.Executing));
        _leases.Setup(l => l.ClaimRunningAsync(7, ServerTaskLeaseOwner.Current, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var ex = await Should.ThrowAsync<ServerTaskOwnedByLiveProcessException>(() => Service().StartExecutingAsync(7));

        ex.LeaseOwner.ShouldBe("winner:1:x");
    }

    // The winner's pipeline died right after claiming and its lease went stale: the loser may legitimately take over.
    [Fact]
    public async Task LostClaim_ToADeadWinner_IsTakenOver()
    {
        Reads(TaskState.Pending);
        _tasks.Setup(t => t.TransitionStateAsync(7, TaskState.Pending, TaskState.Executing, ServerTaskLeaseOwner.Current, It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskStateTransitionException(TaskState.Executing, TaskState.Executing));
        _leases.Setup(l => l.ClaimRunningAsync(7, ServerTaskLeaseOwner.Current, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        (await Service().StartExecutingAsync(7)).Task.State.ShouldBe(TaskState.Executing);
    }

    // Anything but "someone moved it to Executing" keeps the original transition error.
    [Theory]
    [InlineData(TaskState.Cancelled)]
    [InlineData(TaskState.Success)]
    public async Task LostClaim_ToAnyOtherState_KeepsTheTransitionError(string actual)
    {
        Reads(TaskState.Pending);
        _tasks.Setup(t => t.TransitionStateAsync(7, TaskState.Pending, TaskState.Executing, ServerTaskLeaseOwner.Current, It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskStateTransitionException(actual, TaskState.Executing));

        await Should.ThrowAsync<ServerTaskStateTransitionException>(() => Service().StartExecutingAsync(7));

        _leases.Verify(l => l.ClaimRunningAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A duplicate delivery to a task being cancelled used to fall into the transition error, which the runner turns into Cancelling -> Failed on the owner's task.
    // The owner is finishing the cancel (or the reaper completes it if it died), so the delivery is dropped whatever the lease says.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeliveryToACancellingTask_IsFenced_NeverTakenOver_NeverAnError(bool leaseIsStale)
    {
        _tasks.SetupSequence(t => t.GetServerTaskByIdNoTrackingAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 7, State = TaskState.Cancelling }).ReturnsAsync(new ServerTaskEntity { Id = 7, State = TaskState.Cancelling, LeaseOwner = "owner:1:x" });
        _leases.Setup(l => l.ClaimRunningAsync(7, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(leaseIsStale ? 1 : 0);

        var ex = await Should.ThrowAsync<ServerTaskOwnedByLiveProcessException>(() => Service().StartExecutingAsync(7));

        ex.LeaseOwner.ShouldBe("owner:1:x");
        _leases.Verify(l => l.ClaimRunningAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never, "a second pipeline must never start on a cancelled task");
        _tasks.Verify(t => t.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Taking over a dead owner's Executing row continues a deployment that already started: it is a resume, so a resolved guided failure applies.
    [Theory]
    [InlineData(TaskState.Pending, false, false)]   // a fresh claim
    [InlineData(TaskState.Paused, false, true)]     // a resume
    [InlineData(TaskState.Executing, true, true)]   // a takeover of a dead owner
    [InlineData(TaskState.Pending, true, true)]     // a lost claim race that then takes over a dead winner
    public async Task IsResumed_IsTrueForAResumeAndForATakeoverOfARunningRow(string state, bool takesOverARunningRow, bool expectedResumed)
    {
        Reads(state);
        if (state != TaskState.Executing && takesOverARunningRow)
            _tasks.Setup(t => t.TransitionStateAsync(7, state, TaskState.Executing, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskStateTransitionException(TaskState.Executing, TaskState.Executing));
        _leases.Setup(l => l.ClaimRunningAsync(7, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await Service().StartExecutingAsync(7);

        result.IsResumed.ShouldBe(expectedResumed);
    }

    private void Reads(string state)
        => _tasks.Setup(t => t.GetServerTaskByIdNoTrackingAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 7, State = state });

    private ServerTaskService Service() => new(_tasks.Object, Mock.Of<IActivityLogDataProvider>(), Mock.Of<IServerTaskLogDataProvider>(), _leases.Object, new Squid.Core.Settings.ServerTask.ServerTaskSetting());
}
