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
/// registry cancel never arrives (the cancelling pod's registry is empty), so the runner must consult the task row to tell them apart — a row in
/// Cancelling is a user cancel (cancel the agent script, end Cancelled), a row still Executing is a real shutdown (pause, keep the script).
/// </summary>
public class DeploymentPipelineRunnerExternalCancelArbitrationTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();

    [Fact]
    public async Task ExternalTokenFires_WhileRowIsCancelling_EndsCancelled_AndCancelsTheAgentScript()
    {
        SetRowState(TaskState.Cancelling);
        using var external = new CancellationTokenSource();
        bool? cancelDecision = null;

        await Runner(Phase(external, d => cancelDecision = d)).ProcessAsync(1, external.Token);

        cancelDecision.ShouldBe(true, "a user cancel delivered as a job abort must cancel the agent's script, not leave it running");
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExternalTokenFires_WhileRowIsExecuting_IsARealShutdown_PausesAndKeepsTheScript()
    {
        SetRowState(TaskState.Executing);
        using var external = new CancellationTokenSource();
        bool? cancelDecision = null;

        await Should.ThrowAsync<OperationCanceledException>(() => Runner(Phase(external, d => cancelDecision = d)).ProcessAsync(1, external.Token));

        cancelDecision.ShouldBe(false);
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
        bool? cancelDecision = null;

        await Should.ThrowAsync<OperationCanceledException>(() => Runner(Phase(external, d => cancelDecision = d)).ProcessAsync(1, external.Token));

        cancelDecision.ShouldBe(false);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void SetRowState(string state)
        => _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 1, State = state });

    private DeploymentPipelineRunner Runner(IDeploymentPipelinePhase phase)
        => new([phase], _lifecycle.Object, _completion.Object, new TaskCancellationRegistry(), _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

    // Plays the observer: fires the external token (Hangfire's abort / the host stopping), waits for the runner's token, then reads the decision.
    private static IDeploymentPipelinePhase Phase(CancellationTokenSource external, Action<bool> recordDecision)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).Returns<DeploymentTaskContext, CancellationToken>(async (ctx, ct) =>
        {
            external.Cancel();

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
