using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// A run that loses its lease must STOP: the lease-lost signal is linked into the pipeline's cancellation, so a run the reaper already recovered
/// (or that another run took over) stops dispatching and writing instead of working on beside the new owner. Deleting the link from the runner
/// leaves the running phase blocked forever and fails these.
/// </summary>
public class DeploymentPipelineRunnerLeaseLossTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly LossLease _lease = new();

    [Fact]
    public async Task LeaseLostMidRun_StopsTheRunningPhase_RecordsNothing_AndLeavesRemoteScriptsRunning()
    {
        var blocked = new BlockingPhase();
        var run = Runner(new ClaimPhase(_lease), blocked).ProcessAsync(42, CancellationToken.None);
        await blocked.Started.WaitAsync(Bound);

        _lease.Lose();

        await run.WaitAsync(Bound);

        blocked.DecisionWhenStopped.ShouldBe(false, "the new owner decides what happens to the agent scripts; this run must not cancel them");
        VerifyNoOutcomeRecorded();
        _lease.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task LeaseLostMidRun_EvenWithAUserCancel_StillRecordsNothing()
    {
        var blocked = new BlockingPhase();
        var run = Runner(new ClaimPhase(_lease), blocked).ProcessAsync(42, CancellationToken.None);
        await blocked.Started.WaitAsync(Bound);

        _lease.Lose();
        _registry.TryCancel(42);

        await run.WaitAsync(Bound);

        blocked.DecisionWhenStopped.ShouldBe(false, "a lost lease means someone else owns the task and its scripts, whatever the user asked of this run");
        VerifyNoOutcomeRecorded();
    }

    // The signal fires from the keeper's own loop, possibly before the runner has finished wiring it: a lease that is already lost when the claim phase
    // returns must stop the run at once.
    [Fact]
    public async Task LeaseAlreadyLostWhenTheClaimReturns_StopsTheNextPhaseImmediately()
    {
        await Runner(new ClaimPhase(_lease, loseDuringClaim: true), new BlockingPhase()).ProcessAsync(42, CancellationToken.None).WaitAsync(Bound);

        VerifyNoOutcomeRecorded();
    }

    // A run that is NOT being stopped for losing its lease records its outcome as before.
    [Fact]
    public async Task HeldLease_DoesNotStopTheRun_AndTheOutcomeIsRecorded()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).Callback<DeploymentTaskContext, CancellationToken>((ctx, _) => ctx.Lease = _lease).Returns(Task.CompletedTask);

        await Runner(phase.Object).ProcessAsync(42, CancellationToken.None);

        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyNoOutcomeRecorded()
    {
        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private DeploymentPipelineRunner Runner(params IDeploymentPipelinePhase[] phases)
        => new(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

    // Stands in for LoadTaskPhase: the claim succeeded, so the context holds a lease (the runner wires its lost signal once this phase returns).
    private sealed class ClaimPhase(LossLease lease, bool loseDuringClaim = false) : IDeploymentPipelinePhase
    {
        public int Order => 1;

        public Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
        {
            ctx.Lease = lease;

            if (loseDuringClaim) lease.Lose();

            return Task.CompletedTask;
        }
    }

    // Stands in for the long-running ExecuteStepsPhase: blocks until the run is stopped, then reads the remote-script decision exactly as the
    // observer's cancellation handler does.
    private sealed class BlockingPhase : IDeploymentPipelinePhase
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Order => 2;

        public Task Started => _started.Task;

        public bool? DecisionWhenStopped { get; private set; }

        public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
        {
            _started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                DecisionWhenStopped = ctx.ShouldCancelRemoteScript();
                throw;
            }
        }
    }

    private sealed class LossLease : IServerTaskLease
    {
        private readonly CancellationTokenSource _lost = new();

        public bool IsLost { get; private set; }

        public bool Disposed { get; private set; }

        public CancellationToken LostToken => _lost.Token;

        public void Release()
        {
        }

        public void Lose()
        {
            IsLost = true;

            _lost.Cancel();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
