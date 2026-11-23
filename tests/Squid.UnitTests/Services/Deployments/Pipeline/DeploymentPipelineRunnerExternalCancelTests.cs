using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// A user cancel issued on another pod reaches the running pod only as Hangfire's job abort: the SAME external token a host shutdown fires. The
/// registry cancel never arrives (the cancelling pod's registry is empty), so the runner reads the task row — asynchronously, in its catch, never
/// on the cancellation callback thread — to tell them apart: Cancelling means a user cancel (complete it through OnCancelledAsync, which cancels
/// the agent scripts itself), a row still Executing is a real shutdown (pause, rethrow).
/// </summary>
public class DeploymentPipelineRunnerExternalCancelTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();

    [Fact]
    public async Task ExternalTokenFires_WhileRowIsCancelling_EndsCancelled_EvenThoughTheObserverDecidedPausing()
    {
        SetRowState(TaskState.Cancelling);
        using var external = new CancellationTokenSource();
        bool? observerDecision = null;

        await Runner(Phase(external.Cancel, d => observerDecision = d)).ProcessAsync(1, external.Token);

        observerDecision.ShouldBe(false, "the observer saw only the external token and decided 'pausing'; the terminal cancel must not depend on that");
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExternalTokenFires_WhileRowIsExecuting_IsARealShutdown_PausesAndRethrows()
    {
        SetRowState(TaskState.Executing);
        using var external = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => Runner(Phase(external.Cancel, _ => { })).ProcessAsync(1, external.Token));

        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // An unreadable row must not turn an ordinary shutdown into a cancel: unknown stays the established shutdown behaviour.
    [Fact]
    public async Task ExternalTokenFires_WhenTheRowCannotBeRead_StaysAShutdownPause()
    {
        _taskDataProvider.SetupSequence(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerTaskEntity { Id = 1, State = TaskState.Executing })
            .ThrowsAsync(new InvalidOperationException("db down"));
        using var external = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => Runner(Phase(external.Cancel, _ => { })).ProcessAsync(1, external.Token));

        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Hangfire fires the abort from its ServerJobCancellationWatcher thread; anything blocking inside the cancellation callbacks stalls that
    // watcher for every other aborted job. The row read is pending here and never completes until released: Cancel() must still return promptly.
    [Fact]
    public async Task TheAbortCallback_NeverBlocksOnTheDatabase()
    {
        var read = new TaskCompletionSource<ServerTaskEntity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsStarted = 0;
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref readsStarted) == 1 ? Task.FromResult(new ServerTaskEntity { Id = 1, State = TaskState.Executing }) : read.Task);
        using var external = new CancellationTokenSource();
        var cancelReturned = false;

        var run = Runner(Phase(() =>
        {
            Task.Run(() => external.Cancel()).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            cancelReturned = true;
        }, _ => { })).ProcessAsync(1, external.Token);

        await Task.Delay(500);
        read.SetResult(new ServerTaskEntity { Id = 1, State = TaskState.Executing });
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        cancelReturned.ShouldBeTrue("the abort callback must return while the row read is still pending");
    }

    private void SetRowState(string state)
        => _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 1, State = state });

    private DeploymentPipelineRunner Runner(IDeploymentPipelinePhase phase)
        => new([phase], _lifecycle.Object, _completion.Object, new TaskCancellationRegistry(), _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

    // Plays the observer: fires the external token (Hangfire's abort / the host stopping), waits for the runner's token, then reads the decision.
    private static IDeploymentPipelinePhase Phase(Action fireExternal, Action<bool> recordDecision)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).Returns<DeploymentTaskContext, CancellationToken>(async (ctx, ct) =>
        {
            fireExternal();

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                recordDecision(ctx.ShouldCancelRemoteScript());
                throw;
            }
        });

        return phase.Object;
    }
}
