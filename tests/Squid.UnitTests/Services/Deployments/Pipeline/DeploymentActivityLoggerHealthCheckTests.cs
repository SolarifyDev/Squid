using System;
using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentActivityLoggerHealthCheckTests
{
    [Fact]
    public async Task HealthCheckStarting_LogsInfoMessage()
    {
        var (logger, logWriter) = CreateLogger();

        await logger.HandleAsync(new HealthCheckStartingEvent(new DeploymentEventContext { StepDisplayOrder = 1 }), CancellationToken.None);

        VerifyLogMessage(logWriter, ServerTaskLogCategory.Info, "Running health check");
    }

    [Fact]
    public async Task HealthCheckTargetResult_Healthy_LogsInfo()
    {
        var (logger, logWriter) = CreateLogger();

        await logger.HandleAsync(new HealthCheckTargetResultEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, MachineName = "node-1",
            HealthCheckHealthy = true, HealthCheckDetail = "ok"
        }), CancellationToken.None);

        VerifyLogMessage(logWriter, ServerTaskLogCategory.Info, "Health check passed for node-1: ok");
    }

    [Fact]
    public async Task HealthCheckTargetResult_Unhealthy_LogsWarning()
    {
        var (logger, logWriter) = CreateLogger();

        await logger.HandleAsync(new HealthCheckTargetResultEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, MachineName = "node-2",
            HealthCheckHealthy = false, HealthCheckDetail = "connection refused"
        }), CancellationToken.None);

        VerifyLogMessage(logWriter, ServerTaskLogCategory.Warning, "Health check failed for node-2: connection refused");
    }

    [Fact]
    public async Task HealthCheckCompleted_AllHealthy_LogsInfo()
    {
        var (logger, logWriter) = CreateLogger();

        await logger.HandleAsync(new HealthCheckCompletedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, HealthCheckHealthyCount = 3, HealthCheckUnhealthyCount = 0
        }), CancellationToken.None);

        VerifyLogMessage(logWriter, ServerTaskLogCategory.Info, "Health check completed: all 3 target(s) healthy");
    }

    [Fact]
    public async Task HealthCheckCompleted_SomeUnhealthy_LogsWarning()
    {
        var (logger, logWriter) = CreateLogger();

        await logger.HandleAsync(new HealthCheckCompletedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = 1, HealthCheckHealthyCount = 2, HealthCheckUnhealthyCount = 1
        }), CancellationToken.None);

        VerifyLogMessage(logWriter, ServerTaskLogCategory.Warning, "Health check completed: 2 healthy, 1 unhealthy");
    }

    // === Helpers ===

    private static (DeploymentActivityLogger Logger, Mock<IDeploymentLogWriter> LogWriter) CreateLogger()
    {
        var logWriter = new Mock<IDeploymentLogWriter>();

        logWriter.Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new DeploymentActivityLogger(logWriter.Object);
        var ctx = new DeploymentTaskContext { ServerTaskId = 1 };
        logger.Initialize(ctx);

        return (logger, logWriter);
    }

    private static void VerifyLogMessage(Mock<IDeploymentLogWriter> logWriter, ServerTaskLogCategory category, string expectedMessage)
    {
        logWriter.Verify(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), category, expectedMessage, It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
