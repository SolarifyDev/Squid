using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Wiring between the runner and the lease: a guard tested alone proves nothing if the call site is deleted. Removing
/// <c>leaseKeeper.Start</c> from the runner, or the disposal in its finally, fails these.
/// </summary>
public class DeploymentPipelineRunnerLeaseTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly RecordingLeaseKeeper _keeper = new();

    [Fact]
    public async Task RunningTask_StartsTheLeaseForThatTask_ThenReleasesItOnSuccess()
    {
        var phaseSawLease = false;

        await Runner(Phase((ctx, _) => phaseSawLease = _keeper.Started.Count == 1 && !_keeper.Leases[0].Disposed)).ProcessAsync(42, CancellationToken.None);

        _keeper.Started.ShouldBe([42]);
        phaseSawLease.ShouldBeTrue("the lease must be running while the pipeline's phases execute");
        _keeper.Leases[0].Disposed.ShouldBeTrue();
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeaseIsReleased_WhenAStepFails()
    {
        await Runner(Phase((ctx, _) => ctx.FailureEncountered = true)).ProcessAsync(42, CancellationToken.None);

        _keeper.Leases[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task LeaseIsReleased_WhenThePipelinePauses()
    {
        await Runner(Phase((_, _) => throw new DeploymentSuspendedException(42))).ProcessAsync(42, CancellationToken.None);

        _keeper.Leases[0].Disposed.ShouldBeTrue();
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeaseIsReleased_WhenThePipelineThrows()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => Runner(Phase((_, _) => throw new InvalidOperationException("boom"))).ProcessAsync(42, CancellationToken.None));

        _keeper.Leases[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task LeaseIsReleased_WhenTheTaskIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Runner(Phase((_, ct) => ct.ThrowIfCancellationRequested())).ProcessAsync(42, cts.Token);

        _keeper.Leases[0].Disposed.ShouldBeTrue();
    }

    // A task that never ran (already finished, or waiting for its environment's slot) must not take a lease.
    [Fact]
    public async Task NoLease_ForATaskThatIsAlreadyResolved()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 42, State = TaskState.Success });

        await Runner(Phase((_, _) => { })).ProcessAsync(42, CancellationToken.None);

        _keeper.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoLease_ForATaskWaitingForItsSlot()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 42, State = TaskState.Pending, ConcurrencyTag = "deploy:env-1" });
        _taskDataProvider.Setup(p => p.HasActiveTaskWithTagAsync("deploy:env-1", 42, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Runner(Phase((_, _) => { })).ProcessAsync(42, CancellationToken.None);

        _keeper.Started.ShouldBeEmpty();
    }

    // The reaper already paused the task: recording the pipeline's own outcome would flip that Paused row to Failed and delete its checkpoint.
    [Fact]
    public async Task LostLease_RecordsNoOutcome_SoTheReapersDecisionStands()
    {
        _keeper.LoseOnStart = true;

        await Runner(Phase((_, _) => { })).ProcessAsync(42, CancellationToken.None);

        VerifyNoOutcomeRecorded();
    }

    [Fact]
    public async Task LostLease_ThenAFailingPipeline_DoesNotOverwriteTheRecoveredState()
    {
        _keeper.LoseOnStart = true;

        await Should.ThrowAsync<InvalidOperationException>(() => Runner(Phase((_, _) => throw new InvalidOperationException("script died"))).ProcessAsync(42, CancellationToken.None));

        VerifyNoOutcomeRecorded();
        _keeper.Leases[0].Disposed.ShouldBeTrue();
    }

    private void VerifyNoOutcomeRecorded()
    {
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private DeploymentPipelineRunner Runner(IDeploymentPipelinePhase phase)
        => new([phase], _lifecycle.Object, _completion.Object, new TaskCancellationRegistry(), _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>(), _keeper);

    private static IDeploymentPipelinePhase Phase(Action<DeploymentTaskContext, CancellationToken> body)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).Returns<DeploymentTaskContext, CancellationToken>((ctx, ct) => { body(ctx, ct); return Task.CompletedTask; });

        return phase.Object;
    }

    private sealed class RecordingLeaseKeeper : IServerTaskLeaseKeeper
    {
        public List<int> Started { get; } = new();

        public List<RecordingLease> Leases { get; } = new();

        public bool LoseOnStart { get; set; }

        public IServerTaskLease Start(int serverTaskId)
        {
            Started.Add(serverTaskId);

            var lease = new RecordingLease { IsLost = LoseOnStart };
            Leases.Add(lease);

            return lease;
        }
    }

    private sealed class RecordingLease : IServerTaskLease
    {
        public bool IsLost { get; set; }

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
