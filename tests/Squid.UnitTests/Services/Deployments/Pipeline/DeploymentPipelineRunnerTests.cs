using System;
using System.Diagnostics;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;

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
        var runner = new DeploymentPipelineRunner(Enumerable.Empty<IDeploymentPipelinePhase>(), lifecycle.Object, completion.Object, registry, _taskDataProvider.Object);

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
        var runner = new DeploymentPipelineRunner(Enumerable.Empty<IDeploymentPipelinePhase>(), lifecycle.Object, completion.Object, registry, _taskDataProvider.Object);

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
        var runner = new DeploymentPipelineRunner(new[] { failurePhase.Object }, lifecycle.Object, completion.Object, registry, _taskDataProvider.Object);

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
        var runner = new DeploymentPipelineRunner(new[] { okPhase.Object }, lifecycle.Object, completion.Object, registry, _taskDataProvider.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
