using System;
using System.Diagnostics;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentPipelineRunnerTests
{
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
        var runner = new DeploymentPipelineRunner(Enumerable.Empty<IDeploymentPipelinePhase>(), lifecycle.Object, completion.Object, registry);

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
        var runner = new DeploymentPipelineRunner(Enumerable.Empty<IDeploymentPipelinePhase>(), lifecycle.Object, completion.Object, registry);

        var sw = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(() => runner.ProcessAsync(1, CancellationToken.None));
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45));
    }
}
