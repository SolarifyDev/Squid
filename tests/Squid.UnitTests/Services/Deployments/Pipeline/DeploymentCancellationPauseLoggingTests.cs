using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentCancellationPauseLoggingTests
{
    private record CapturedLog(ServerTaskLogCategory Category, string Message, string Source, long? ActivityNodeId);

    [Fact]
    public async Task DeploymentCancelled_LogsWarningAndUpdatesNodeToFailed()
    {
        var (lifecycle, logs, nodes, logWriter) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);
        await lifecycle.EmitAsync(new DeploymentCancelledEvent(new DeploymentEventContext()), CancellationToken.None);

        var cancelLog = logs.FirstOrDefault(l => l.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        cancelLog.ShouldNotBeNull("Should log deployment cancelled message");
        cancelLog.Category.ShouldBe(ServerTaskLogCategory.Warning);

        logWriter.Verify(s => s.UpdateActivityNodeStatusAsync(It.IsAny<long>(), DeploymentActivityLogNodeStatus.Failed, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeploymentPaused_LogsInfoMessage()
    {
        var (lifecycle, logs, _, _) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);
        await lifecycle.EmitAsync(new DeploymentPausedEvent(new DeploymentEventContext()), CancellationToken.None);

        var pauseLog = logs.FirstOrDefault(l => l.Message.Contains("paused", StringComparison.OrdinalIgnoreCase));
        pauseLog.ShouldNotBeNull("Should log deployment paused message");
        pauseLog.Category.ShouldBe(ServerTaskLogCategory.Info);
    }

    [Fact]
    public async Task DeploymentPaused_DoesNotUpdateNodeStatusToFailed()
    {
        var (lifecycle, _, _, logWriter) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);
        await lifecycle.EmitAsync(new DeploymentPausedEvent(new DeploymentEventContext()), CancellationToken.None);

        logWriter.Verify(s => s.UpdateActivityNodeStatusAsync(It.IsAny<long>(), DeploymentActivityLogNodeStatus.Failed, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========== Test Infrastructure ==========

    private static (IDeploymentLifecycle Lifecycle, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes, Mock<IDeploymentLogWriter> LogWriter) CreateLifecycleHarness()
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var nextNodeId = 0L;
        var logWriter = new Mock<IDeploymentLogWriter>();

        logWriter
            .Setup(x => x.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
            {
                var node = new ActivityLog
                {
                    Id = Interlocked.Increment(ref nextNodeId),
                    ServerTaskId = taskId, ParentId = parentId, Name = name,
                    NodeType = nodeType, Status = status, SortOrder = sortOrder,
                    StartedAt = DateTimeOffset.UtcNow
                };
                nodes.Add(node);
                return node;
            });

        logWriter
            .Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, long seq, ServerTaskLogCategory cat, string msg, string src, long? nodeId, DateTimeOffset? at, CancellationToken _) =>
            {
                logs.Add(new CapturedLog(cat, msg, src, nodeId));
            })
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.FlushAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.GetTreeByTaskIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog>());

        var logger = new DeploymentActivityLogger(logWriter.Object);
        var lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });

        return (lifecycle, logs, nodes, logWriter);
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            ServerTaskId = 1001,
            Task = new Squid.Core.Persistence.Entities.Deployments.ServerTask { Id = 1001 },
            Deployment = new Deployment { Id = 2001, EnvironmentId = 1, ChannelId = 1 },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Id = 3001, Version = "1.0.0" },
            Variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>(),
            SelectedPackages = new List<Squid.Core.Persistence.Entities.Deployments.ReleaseSelectedPackage>()
        };
    }
}
