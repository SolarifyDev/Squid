using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentPipelineRunnerCancellationTests
{
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly Mock<IDeploymentCompletionHandler> _completion = new();
    private readonly TaskCancellationRegistry _registry = new();

    [Fact]
    public async Task Success_CallsOnSuccessAndUnregisters()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _completion.Verify(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _registry.TryCancel(1).ShouldBeFalse();
    }

    [Fact]
    public async Task Suspended_CallsOnPausedAndDoesNotRethrow()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DeploymentSuspendedException(1));
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentPausedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationViaRegistry_CallsOnCancelledAndDoesNotRethrow()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentTaskContext, CancellationToken>(async (_, ct) =>
            {
                _registry.TryCancel(1);
                ct.ThrowIfCancellationRequested();
            });
        var runner = CreateRunner(phase.Object);

        await runner.ProcessAsync(1, CancellationToken.None);

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentCancelledEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Failure_CallsOnFailureAndRethrows()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var runner = CreateRunner(phase.Object);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.ProcessAsync(1, CancellationToken.None));

        _lifecycle.Verify(l => l.EmitAsync(It.IsAny<DeploymentFailedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _completion.Verify(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AlwaysUnregisters_EvenOnFailure()
    {
        var phase = new Mock<IDeploymentPipelinePhase>();
        phase.Setup(p => p.Order).Returns(1);
        phase.Setup(p => p.ExecuteAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var runner = CreateRunner(phase.Object);

        try { await runner.ProcessAsync(1, CancellationToken.None); } catch { }

        _registry.TryCancel(1).ShouldBeFalse();
    }

    private DeploymentPipelineRunner CreateRunner(params IDeploymentPipelinePhase[] phases)
    {
        return new DeploymentPipelineRunner(phases, _lifecycle.Object, _completion.Object, _registry);
    }
}
