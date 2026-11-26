using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// A user's cancel wins over whatever ends the run. In a required manual-intervention or guided-failure step a pause write refused because the row is
/// already Cancelling reaches the runner as a plain exception; resolving that as Failed would end the user's cancel Failed (Cancelling -> Failed is legal
/// while the run holds the lease) and leave the agent scripts running. The runner reads the row and completes the cancel instead.
/// </summary>
public class DeploymentPipelineRunnerCancelWinsTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = CompletionHandlerMocks.ReportingIntendedOutcomes();

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task AnErrorEndingTheRun_WhileTheRowIsCancelling_CompletesTheCancel_AndDoesNotRethrow(Exception error)
    {
        SetRowState(TaskState.Cancelling);

        await Should.NotThrowAsync(() => Runner(Throwing(error)).ProcessAsync(1, CancellationToken.None));

        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task AnErrorEndingTheRun_WhileTheRowIsExecuting_StillFailsAndRethrows(Exception error)
    {
        SetRowState(TaskState.Executing);

        await Should.ThrowAsync<Exception>(() => Runner(Throwing(error)).ProcessAsync(1, CancellationToken.None));

        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), error, It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // An unreadable row must not turn a real failure into a cancel.
    [Fact]
    public async Task AnErrorEndingTheRun_WhenTheRowCannotBeRead_StillFails()
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        await Should.ThrowAsync<Exception>(() => Runner(Throwing(new InvalidOperationException("boom"))).ProcessAsync(1, CancellationToken.None));

        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static IEnumerable<object[]> Errors() =>
    [
        [new ServerTaskStateTransitionException(TaskState.Cancelling, TaskState.Paused)],
        [new InvalidOperationException("an action failed")]
    ];

    private void SetRowState(string state)
        => _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 1, State = state });

    private DeploymentPipelineRunner Runner(IDeploymentPipelinePhase phase)
        => new([phase], _lifecycle.Object, _completion.Object, new TaskCancellationRegistry(), _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

    private static IDeploymentPipelinePhase Throwing(Exception error)
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(error);

        return phase.Object;
    }
}
