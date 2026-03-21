using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentPipelineRunnerTimeoutTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();
    private readonly Mock<IServerTaskDataProvider> _taskDataProvider = new();

    [Fact]
    public async Task Timeout_CallsOnFailure_WithDeploymentTimeoutException()
    {
        var phase = CreateHangingPhase();
        var runner = CreateRunner(phase);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Timeout_EmitsDeploymentTimedOutEvent()
    {
        var phase = CreateHangingPhase();
        var runner = CreateRunner(phase);

        await runner.ProcessAsync(1, CancellationToken.None);

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentTimedOutEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Timeout_DoesNotRethrow()
    {
        var phase = CreateHangingPhase();
        var runner = CreateRunner(phase);

        await Should.NotThrowAsync(() => runner.ProcessAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_Unregisters()
    {
        var phase = CreateHangingPhase();
        var runner = CreateRunner(phase);

        await runner.ProcessAsync(1, CancellationToken.None);

        _registry.TryCancel(1).ShouldBeFalse();
    }

    [Fact]
    public async Task UserCancel_DuringTimeout_TreatedAsCancellation()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, ct) =>
            {
                _registry.TryCancel(1);
                await Task.Delay(Timeout.Infinite, ct);
            });
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<DeploymentTimeoutException>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private IDeploymentPipelinePhase CreateHangingPhase()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
            });

        return phase.Object;
    }

    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry, _taskDataProvider.Object)
        {
            DeploymentTimeout = TimeSpan.FromMilliseconds(50)
        };
    }
}
