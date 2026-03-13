using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentActivityLoggerResumeTests
{
    [Fact]
    public async Task Resume_ExistingTaskNode_RestoresAndUpdatesStatus()
    {
        var serverTaskService = new Mock<IServerTaskService>();
        var activityLogDataProvider = new Mock<IActivityLogDataProvider>();

        activityLogDataProvider.Setup(p => p.GetTreeByTaskIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog>
            {
                new() { Id = 42, ServerTaskId = 1, NodeType = DeploymentActivityLogNodeType.Task }
            });

        var logger = new DeploymentActivityLogger(serverTaskService.Object, activityLogDataProvider.Object);
        var ctx = new DeploymentTaskContext { ServerTaskId = 1 };
        logger.Initialize(ctx);

        await logger.HandleAsync(new DeploymentResumingEvent(new DeploymentEventContext()), CancellationToken.None);

        serverTaskService.Verify(s => s.UpdateActivityNodeStatusAsync(42, DeploymentActivityLogNodeStatus.Running, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        serverTaskService.Verify(s => s.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), "Resumed deployment", It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resume_NoExistingTaskNode_CreatesFallback()
    {
        var serverTaskService = new Mock<IServerTaskService>();
        serverTaskService.Setup(s => s.AddActivityNodeAsync(1, null, "Resumed deployment", DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityLog { Id = 99 });

        var activityLogDataProvider = new Mock<IActivityLogDataProvider>();
        activityLogDataProvider.Setup(p => p.GetTreeByTaskIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog>());

        var logger = new DeploymentActivityLogger(serverTaskService.Object, activityLogDataProvider.Object);
        var ctx = new DeploymentTaskContext { ServerTaskId = 1 };
        logger.Initialize(ctx);

        await logger.HandleAsync(new DeploymentResumingEvent(new DeploymentEventContext()), CancellationToken.None);

        serverTaskService.Verify(s => s.AddActivityNodeAsync(1, null, "Resumed deployment", DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_ExistingStepNode_ReusesOnStepStarting()
    {
        var serverTaskService = new Mock<IServerTaskService>();
        var activityLogDataProvider = new Mock<IActivityLogDataProvider>();

        activityLogDataProvider.Setup(p => p.GetTreeByTaskIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog>
            {
                new() { Id = 42, ServerTaskId = 1, NodeType = DeploymentActivityLogNodeType.Task },
                new() { Id = 100, ServerTaskId = 1, NodeType = DeploymentActivityLogNodeType.Step, SortOrder = 2 }
            });

        var logger = new DeploymentActivityLogger(serverTaskService.Object, activityLogDataProvider.Object);
        var ctx = new DeploymentTaskContext { ServerTaskId = 1 };
        logger.Initialize(ctx);

        await logger.HandleAsync(new DeploymentResumingEvent(new DeploymentEventContext()), CancellationToken.None);
        await logger.HandleAsync(new StepStartingEvent(new DeploymentEventContext { StepName = "Approval", StepDisplayOrder = 2 }), CancellationToken.None);

        serverTaskService.Verify(s => s.UpdateActivityNodeStatusAsync(100, DeploymentActivityLogNodeStatus.Running, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        serverTaskService.Verify(s => s.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.Is<string>(n => n.Contains("Approval")), DeploymentActivityLogNodeType.Step, It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resume_DbLookupThrows_CreatesFallback()
    {
        var serverTaskService = new Mock<IServerTaskService>();
        serverTaskService.Setup(s => s.AddActivityNodeAsync(1, null, "Resumed deployment", DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityLog { Id = 99 });

        var activityLogDataProvider = new Mock<IActivityLogDataProvider>();
        activityLogDataProvider.Setup(p => p.GetTreeByTaskIdAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB connection failed"));

        var logger = new DeploymentActivityLogger(serverTaskService.Object, activityLogDataProvider.Object);
        var ctx = new DeploymentTaskContext { ServerTaskId = 1 };
        logger.Initialize(ctx);

        await Should.NotThrowAsync(() => logger.HandleAsync(new DeploymentResumingEvent(new DeploymentEventContext()), CancellationToken.None));

        serverTaskService.Verify(s => s.AddActivityNodeAsync(1, null, "Resumed deployment", DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0, It.IsAny<CancellationToken>()), Times.Once);
    }
}
