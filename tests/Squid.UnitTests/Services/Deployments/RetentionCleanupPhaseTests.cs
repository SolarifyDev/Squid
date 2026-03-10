using System;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.LifeCycle;

namespace Squid.UnitTests.Services.Deployments;

public class RetentionCleanupPhaseTests
{
    [Fact]
    public async Task ExecuteAsync_CallsEnforcerForProject()
    {
        var enforcer = new Mock<IRetentionPolicyEnforcer>();
        var phase = new RetentionCleanupPhase(enforcer.Object);
        var ctx = new DeploymentTaskContext { Project = new Project { Id = 42 } };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        enforcer.Verify(e => e.EnforceRetentionForProjectAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NullProject_DoesNotCallEnforcer()
    {
        var enforcer = new Mock<IRetentionPolicyEnforcer>();
        var phase = new RetentionCleanupPhase(enforcer.Object);
        var ctx = new DeploymentTaskContext();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        enforcer.Verify(e => e.EnforceRetentionForProjectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EnforcerThrows_DoesNotPropagate()
    {
        var enforcer = new Mock<IRetentionPolicyEnforcer>();
        enforcer.Setup(e => e.EnforceRetentionForProjectAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));
        var phase = new RetentionCleanupPhase(enforcer.Object);
        var ctx = new DeploymentTaskContext { Project = new Project { Id = 1 } };

        var ex = await Record.ExceptionAsync(() => phase.ExecuteAsync(ctx, CancellationToken.None));

        ex.ShouldBeNull();
    }

    [Fact]
    public void Order_Is600()
    {
        var enforcer = new Mock<IRetentionPolicyEnforcer>();
        var phase = new RetentionCleanupPhase(enforcer.Object);

        phase.Order.ShouldBe(600);
    }
}
