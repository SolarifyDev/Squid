using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

internal static class CompletionHandlerMocks
{
    /// <summary>A completion handler whose every callback reports the outcome it set out to write — what the real handler reports when nothing races it.</summary>
    public static Mock<IDeploymentCompletionHandler> ReportingIntendedOutcomes()
    {
        var mock = new Mock<IDeploymentCompletionHandler>();

        mock.Setup(c => c.OnSuccessAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(DeploymentOutcome.Succeeded);
        mock.Setup(c => c.OnFailureAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ReturnsAsync(DeploymentOutcome.Failed);
        mock.Setup(c => c.OnCancelledAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(DeploymentOutcome.Cancelled);
        mock.Setup(c => c.OnPausedAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(DeploymentOutcome.Paused);
        mock.Setup(c => c.OnTimedOutAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ReturnsAsync(DeploymentOutcome.Paused);
        mock.Setup(c => c.OnTransientPauseAsync(It.IsAny<DeploymentTaskContext>(), It.IsAny<Exception>(), It.IsAny<CancellationToken>())).ReturnsAsync(DeploymentOutcome.Paused);

        return mock;
    }
}
