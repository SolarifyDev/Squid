using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentLifecyclePublisherTests
{
    [Fact]
    public async Task EmitAsync_HandlerThrows_ContinuesToNextHandler()
    {
        var handler1 = new Mock<IDeploymentLifecycleHandler>();
        handler1.Setup(h => h.Order).Returns(1);
        handler1.Setup(h => h.HandleAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("handler1 boom"));

        var handler2 = new Mock<IDeploymentLifecycleHandler>();
        handler2.Setup(h => h.Order).Returns(2);

        var publisher = new DeploymentLifecyclePublisher(new[] { handler1.Object, handler2.Object });
        publisher.Initialize(new DeploymentTaskContext());

        await publisher.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), CancellationToken.None);

        handler2.Verify(h => h.HandleAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmitAsync_HandlerThrows_DoesNotPropagateException()
    {
        var handler = new Mock<IDeploymentLifecycleHandler>();
        handler.Setup(h => h.Order).Returns(1);
        handler.Setup(h => h.HandleAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var publisher = new DeploymentLifecyclePublisher(new[] { handler.Object });
        publisher.Initialize(new DeploymentTaskContext());

        await Should.NotThrowAsync(() => publisher.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), CancellationToken.None));
    }

    [Fact]
    public async Task EmitAsync_AllHandlersSucceed_AllReceiveEvent()
    {
        var handler1 = new Mock<IDeploymentLifecycleHandler>();
        handler1.Setup(h => h.Order).Returns(1);

        var handler2 = new Mock<IDeploymentLifecycleHandler>();
        handler2.Setup(h => h.Order).Returns(2);

        var publisher = new DeploymentLifecyclePublisher(new[] { handler1.Object, handler2.Object });
        publisher.Initialize(new DeploymentTaskContext());

        await publisher.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), CancellationToken.None);

        handler1.Verify(h => h.HandleAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        handler2.Verify(h => h.HandleAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
