using System;
using System.Diagnostics;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentPipelineRunnerTests
{
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    [Fact]
    public async Task CompletionHandler_ReceivesCancellableToken_NotCancellationTokenNone()
    {
        CancellationToken capturedToken = default;

        var lifecycle = new Mock<IDeploymentLifecycle>();
        var completion = new Mock<IDeploymentCompletionHandler>();
        completion.Setup(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentTaskContext, CancellationToken>((_, token) => capturedToken = token)
            .Returns(Task.CompletedTask);

        var registry = new TaskCancellationRegistry();
        var runner = new DeploymentPipelineRunner(Enumerable.Empty<IDeploymentPipelinePhase>(), lifecycle.Object, completion.Object, registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

        await runner.ProcessAsync(1, CancellationToken.None);

        capturedToken.CanBeCanceled.ShouldBeTrue();
    }

    [Fact]
    public async Task CompletionHandler_Timeout_DoesNotBlockIndefinitely()
    {
        var lifecycle = new Mock<IDeploymentLifecycle>();
        var completion = new Mock<IDeploymentCompletionHandler>();
        completion.Setup(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, token) => await Task.Delay(TimeSpan.FromSeconds(60), token));

        var registry = new TaskCancellationRegistry();
        var runner = new DeploymentPipelineRunner(Enumerable.Empty<IDeploymentPipelinePhase>(), lifecycle.Object, completion.Object, registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

        var sw = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(() => runner.ProcessAsync(1, CancellationToken.None));
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task FailureEncountered_CallsOnFailure_NotOnSuccess()
    {
        var lifecycle = new Mock<IDeploymentLifecycle>();
        var completion = new Mock<IDeploymentCompletionHandler>();
        completion.Setup(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var failurePhase = new Mock<IDeploymentPipelinePhase>();
        failurePhase.Setup(p => p.Order).Returns(1);
        failurePhase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentTaskContext, CancellationToken>((ctx, _) => ctx.FailureEncountered = true)
            .Returns(Task.CompletedTask);

        var registry = new TaskCancellationRegistry();
        var runner = new DeploymentPipelineRunner(new[] { failurePhase.Object }, lifecycle.Object, completion.Object, registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

        await runner.ProcessAsync(1, CancellationToken.None);

        completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
        completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoFailure_CallsOnSuccess()
    {
        var lifecycle = new Mock<IDeploymentLifecycle>();
        var completion = new Mock<IDeploymentCompletionHandler>();
        completion.Setup(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var okPhase = new Mock<IDeploymentPipelinePhase>();
        okPhase.Setup(p => p.Order).Returns(1);
        okPhase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new TaskCancellationRegistry();
        var runner = new DeploymentPipelineRunner(new[] { okPhase.Object }, lifecycle.Object, completion.Object, registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

        await runner.ProcessAsync(1, CancellationToken.None);

        completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnSuccessThrowingAnIllegalTransition_FallsThroughToOnFailure()
    {
        // OnSuccessAsync transitions from a hardcoded Executing, so a task cancelled mid-pipeline
        // makes it throw. Unlike every other completion callback it is invoked directly rather than
        // through SafeCompleteAsync, so the exception is NOT swallowed — it reaches the general
        // catch, which routes to OnFailureAsync, and that resolves the current state and performs
        // the legal Cancelling -> Failed. That is why a Cancelling row does NOT wedge on the
        // success path, which PauseIfStillExecutingAsync's doc-comment states; pinned here so the
        // claim cannot quietly become false if the runner's call shape changes.
        var lifecycle = new Mock<IDeploymentLifecycle>();
        var completion = new Mock<IDeploymentCompletionHandler>();
        completion.Setup(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServerTaskStateTransitionException(TaskState.Cancelling, TaskState.Success));
        completion.Setup(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var okPhase = new Mock<IDeploymentPipelinePhase>();
        okPhase.Setup(p => p.Order).Returns(1);
        okPhase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new TaskCancellationRegistry();
        var runner = new DeploymentPipelineRunner(new[] { okPhase.Object }, lifecycle.Object, completion.Object, registry, _taskDataProvider.Object, Mock.Of<ISquidBackgroundJobClient>());

        await Should.ThrowAsync<ServerTaskStateTransitionException>(() => runner.ProcessAsync(1, CancellationToken.None));

        completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // An imported Octopus task may keep a non-terminal state (R8). A stray dispatch of it must short-circuit exactly like a
    // terminal task: no slot check, no phases, no completion, no re-enqueue.
    [Theory]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Executing)]
    [InlineData(TaskState.Cancelling)]
    public async Task ImportedTask_AlreadyResolved(string state)
    {
        _taskDataProvider.Setup(p => p.GetServerTaskByIdNoTrackingAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.ServerTask { Id = 1, State = state, ConcurrencyTag = "deploy:env-5" });
        _taskDataProvider.Setup(p => p.IsImportedAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var lifecycle = new Mock<IDeploymentLifecycle>();
        var completion = new Mock<IDeploymentCompletionHandler>();
        var phase = new Mock<IDeploymentPipelinePhase>();
        var jobClient = new Mock<ISquidBackgroundJobClient>();
        var runner = new DeploymentPipelineRunner(new[] { phase.Object }, lifecycle.Object, completion.Object, new TaskCancellationRegistry(), _taskDataProvider.Object, jobClient.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        phase.Verify(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
        lifecycle.Verify(l => l.Initialize(It.IsAny<DeploymentTaskContext>()), Times.Never);
        completion.VerifyNoOtherCalls();
        _taskDataProvider.Verify(p => p.HasActiveTaskWithTagAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        jobClient.VerifyNoOtherCalls();
    }
}
