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
/// Wiring between the runner and the lease: a guard tested alone proves nothing if the call site is deleted. The lease is started by
/// <c>LoadTaskPhase</c> after a successful claim (see <c>LoadTaskPhaseLeaseTests</c>); the runner owns releasing it on every exit path and
/// refusing to record an outcome once it is lost. Removing the disposal in its finally, or either guard, fails these.
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
    public async Task LeaseIsReleased_WhenTheUserCancels()
    {
        var registry = new TaskCancellationRegistry();
        var phase = Phase((_, ct) => { registry.TryCancel(42); ct.ThrowIfCancellationRequested(); });
        var runner = new DeploymentPipelineRunner([phase], _lifecycle.Object, _completion.Object, registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

        await runner.ProcessAsync(42, CancellationToken.None);

        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _keeper.Leases[0].Disposed.ShouldBeTrue();
    }

    // The external job token is the host shutting down: the run pauses and rethrows so Hangfire re-delivers it. The lease must
    // still be released on that path, or the restarted server's claim would be fenced by this dead process's lease until it goes stale.
    [Fact]
    public async Task LeaseIsReleased_WhenTheHostShutsDown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => Runner(Phase((_, ct) => ct.ThrowIfCancellationRequested())).ProcessAsync(42, cts.Token));

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

    // A duplicate delivery of a task a live process still runs: the claim is refused before any lease starts, and nothing at all is written.
    [Fact]
    public async Task DuplicateDeliveryToALiveOwner_IsSuppressed_NothingStartedNothingRecorded()
    {
        var phaseRan = 0;
        var second = new Mock<IDeploymentPipelinePhase>();
        second.Setup(p => p.Order).Returns(2);

        var fenced = PlainPhase((_, _) => { phaseRan++; throw new Squid.Core.Services.Deployments.ServerTask.Exceptions.ServerTaskOwnedByLiveProcessException(42, "pod-a:1:x"); });
        var runner = new DeploymentPipelineRunner([fenced, second.Object], _lifecycle.Object, _completion.Object, new TaskCancellationRegistry(), _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

        await runner.ProcessAsync(42, CancellationToken.None);

        phaseRan.ShouldBe(1);
        second.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never, "no later phase may run");
        _keeper.Started.ShouldBeEmpty("a fenced claim must not start renewing a lease it does not hold");
        VerifyNoOutcomeRecorded();
        _completion.Verify(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void VerifyNoOutcomeRecorded()
    {
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private DeploymentPipelineRunner Runner(IDeploymentPipelinePhase phase)
        => new([phase], _lifecycle.Object, _completion.Object, new TaskCancellationRegistry(), _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

    // Stands in for LoadTaskPhase: the claim succeeded, so the lease starts, then the pipeline's work runs.
    private IDeploymentPipelinePhase Phase(Action<DeploymentTaskContext, CancellationToken> body)
        => PlainPhase((ctx, ct) =>
        {
            ctx.Lease = _keeper.Start(ctx.ServerTaskId);
            body(ctx, ct);
        });

    private static IDeploymentPipelinePhase PlainPhase(Action<DeploymentTaskContext, CancellationToken> body)
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
