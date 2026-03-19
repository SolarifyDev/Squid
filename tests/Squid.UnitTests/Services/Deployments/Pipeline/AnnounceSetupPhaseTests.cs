using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class AnnounceSetupPhaseTests
{
    private record CapturedLog(ServerTaskLogCategory Category, string Message, string Source, long? ActivityNodeId);

    // ========== Fresh Deploy ==========

    [Fact]
    public async Task FreshDeploy_CreatesTaskNode()
    {
        var (phase, ctx, _, nodes) = CreateHarness();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        nodes.ShouldContain(n => n.NodeType == DeploymentActivityLogNodeType.Task);
    }

    [Fact]
    public async Task FreshDeploy_LogsDeployingMessage()
    {
        var (phase, ctx, logs, _) = CreateHarness();
        ctx.Project = new Project { Name = "Smarties.Api" };
        ctx.Environment = new Squid.Core.Persistence.Entities.Deployments.Environment { Name = "TEST" };
        ctx.Release.Version = "2.0.0";

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var log = logs.FirstOrDefault(l => l.Message.Contains("Deploying", StringComparison.OrdinalIgnoreCase));
        log.ShouldNotBeNull("Should log deploying message");
        log.Message.ShouldContain("Smarties.Api");
        log.Message.ShouldContain("2.0.0");
        log.Message.ShouldContain("TEST");
    }

    [Fact]
    public async Task FreshDeploy_LogsTargetsResolved()
    {
        var (phase, ctx, logs, _) = CreateHarness();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var log = logs.FirstOrDefault(l => l.Message.Contains("Found", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("targets", StringComparison.OrdinalIgnoreCase));
        log.ShouldNotBeNull("Should log targets resolved");
        log.Message.ShouldContain("target-1");
    }

    [Fact]
    public async Task FreshDeploy_LogsTargetPreparingPerTarget()
    {
        var (phase, ctx, logs, _) = CreateHarness();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var log = logs.FirstOrDefault(l => l.Message.Contains("Preparing target", StringComparison.OrdinalIgnoreCase));
        log.ShouldNotBeNull("Should log target preparing per target");
        log.Message.ShouldContain("target-1");
    }

    [Fact]
    public async Task FreshDeploy_WithUnhealthyTargets_LogsWarning()
    {
        var (phase, ctx, logs, _) = CreateHarness();
        ctx.ExcludedByHealthTargets = new List<Machine>
        {
            new() { Name = "unhealthy-1", HealthStatus = MachineHealthStatus.Unhealthy }
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var log = logs.FirstOrDefault(l => l.Message.Contains("unhealthy", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("Excluded", StringComparison.OrdinalIgnoreCase));
        log.ShouldNotBeNull("Should log unhealthy targets excluded warning");
        log.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    [Fact]
    public async Task FreshDeploy_WithTransportMissing_LogsWarning()
    {
        var (phase, ctx, logs, _) = CreateHarnessWithMissingTransport();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var log = logs.FirstOrDefault(l => l.Message.Contains("No transport resolved", StringComparison.OrdinalIgnoreCase));
        log.ShouldNotBeNull("Should log transport missing warning");
        log.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    // ========== Resume ==========

    [Fact]
    public async Task Resume_NoExistingNode_CreatesFallbackTaskNode()
    {
        var (phase, ctx, _, nodes) = CreateHarness();
        ctx.IsResume = true;

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        nodes.ShouldContain(n => n.NodeType == DeploymentActivityLogNodeType.Task && n.Name == "Resumed deployment");
    }

    [Fact]
    public async Task Resume_RestoresExistingTaskNode()
    {
        var existingTaskNode = new ActivityLog { Id = 42, NodeType = DeploymentActivityLogNodeType.Task };
        var (phase, ctx, _, _, logWriter) = CreateHarnessWithResumeMocks(existingTaskNode);
        ctx.IsResume = true;

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        logWriter.Verify(s => s.UpdateActivityNodeStatusAsync(42, DeploymentActivityLogNodeStatus.Running, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_LogsResumingMessage()
    {
        var existingTaskNode = new ActivityLog { Id = 42, NodeType = DeploymentActivityLogNodeType.Task };
        var (phase, ctx, logs, _, _) = CreateHarnessWithResumeMocks(existingTaskNode);
        ctx.IsResume = true;

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var log = logs.FirstOrDefault(l => l.Message.Contains("Resuming deployment", StringComparison.OrdinalIgnoreCase));
        log.ShouldNotBeNull("Should log resuming message");
    }

    [Fact]
    public async Task Resume_DoesNotLogDeployingMessage()
    {
        var (phase, ctx, logs, _) = CreateHarness();
        ctx.IsResume = true;

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        logs.ShouldNotContain(l => l.Message.Contains("Deploying", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_DoesNotLogTargetsResolved()
    {
        var (phase, ctx, logs, _) = CreateHarness();
        ctx.IsResume = true;

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        logs.ShouldNotContain(l => l.Message.Contains("Found", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("targets", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_DoesNotLogTargetPreparing()
    {
        var (phase, ctx, logs, _) = CreateHarness();
        ctx.IsResume = true;

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        logs.ShouldNotContain(l => l.Message.Contains("Preparing target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_WithUnhealthyTargets_StillLogsWarning()
    {
        var (phase, ctx, logs, _) = CreateHarness();
        ctx.IsResume = true;
        ctx.ExcludedByHealthTargets = new List<Machine>
        {
            new() { Name = "unhealthy-1", HealthStatus = MachineHealthStatus.Unhealthy }
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var log = logs.FirstOrDefault(l => l.Message.Contains("unhealthy", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("Excluded", StringComparison.OrdinalIgnoreCase));
        log.ShouldNotBeNull("Should still log unhealthy targets on resume");
        log.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    // ========== Regression ==========

    [Fact]
    public async Task Resume_WithoutCheckpoint_StillTakesResumePath()
    {
        // Reproduces the bug: manual intervention at batch 0 → no checkpoint → resume
        // IsResume is derived from task state (Paused), not checkpoint existence
        var (phase, ctx, logs, _) = CreateHarness();
        ctx.IsResume = true;
        ctx.ResumeFromBatchIndex = null; // no checkpoint — suspended before any batch completed

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        logs.ShouldContain(l => l.Message.Contains("Resuming", StringComparison.OrdinalIgnoreCase));
        logs.ShouldNotContain(l => l.Message.Contains("Deploying", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_WithExistingNode_NoDuplicateTaskNodes()
    {
        var existingTaskNode = new ActivityLog { Id = 42, NodeType = DeploymentActivityLogNodeType.Task };
        var (phase, ctx, _, nodes, _) = CreateHarnessWithResumeMocks(existingTaskNode);

        // Resume — should restore existing node, not create a new one
        ctx.IsResume = true;
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        nodes.ShouldNotContain(n => n.NodeType == DeploymentActivityLogNodeType.Task, "Resume with existing node must not create a new task node");
    }

    [Fact]
    public async Task Resume_NoDuplicateLogEntries()
    {
        var (lifecycle, logs, nodes, _) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        // Fresh deploy
        var phase = new AnnounceSetupPhase(lifecycle);
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var deployingCountAfterFresh = logs.Count(l => l.Message.Contains("Deploying", StringComparison.OrdinalIgnoreCase));
        var targetsCountAfterFresh = logs.Count(l => l.Message.Contains("Found", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("targets", StringComparison.OrdinalIgnoreCase));
        var preparingCountAfterFresh = logs.Count(l => l.Message.Contains("Preparing target", StringComparison.OrdinalIgnoreCase));

        // Resume — should not duplicate any fresh deploy logs
        ctx.IsResume = true;
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        logs.Count(l => l.Message.Contains("Deploying", StringComparison.OrdinalIgnoreCase)).ShouldBe(deployingCountAfterFresh, "Resume must not duplicate 'Deploying' log");
        logs.Count(l => l.Message.Contains("Found", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("targets", StringComparison.OrdinalIgnoreCase)).ShouldBe(targetsCountAfterFresh, "Resume must not duplicate 'Found targets' log");
        logs.Count(l => l.Message.Contains("Preparing target", StringComparison.OrdinalIgnoreCase)).ShouldBe(preparingCountAfterFresh, "Resume must not duplicate 'Preparing target' log");
        logs.ShouldContain(l => l.Message.Contains("Resuming", StringComparison.OrdinalIgnoreCase));
    }

    // ========== Test Infrastructure ==========

    private static (AnnounceSetupPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateHarness()
    {
        var (lifecycle, logs, nodes, _) = CreateLifecycleHarness();
        var phase = new AnnounceSetupPhase(lifecycle);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static (AnnounceSetupPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateHarnessWithMissingTransport()
    {
        var (lifecycle, logs, nodes, _) = CreateLifecycleHarness();
        var phase = new AnnounceSetupPhase(lifecycle);
        var ctx = CreateBaseContext();

        // Add a target context with null transport
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            new()
            {
                Machine = new Machine { Name = "no-transport-target" },
                Transport = null,
                CommunicationStyle = CommunicationStyle.KubernetesApi
            }
        };

        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static (AnnounceSetupPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes, Mock<IDeploymentLogWriter> LogWriter) CreateHarnessWithResumeMocks(ActivityLog existingTaskNode)
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var logWriter = CreateLogWriterMock(logs, nodes);

        logWriter
            .Setup(x => x.GetTreeByTaskIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog> { existingTaskNode });

        var logger = new DeploymentActivityLogger(logWriter.Object);
        var lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });
        var phase = new AnnounceSetupPhase(lifecycle);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes, logWriter);
    }

    private static (IDeploymentLifecycle Lifecycle, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes, Mock<IDeploymentLogWriter> LogWriter) CreateLifecycleHarness()
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var logWriter = CreateLogWriterMock(logs, nodes);

        var logger = new DeploymentActivityLogger(logWriter.Object);
        var lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });

        return (lifecycle, logs, nodes, logWriter);
    }

    private static Mock<IDeploymentLogWriter> CreateLogWriterMock(ConcurrentBag<CapturedLog> logs, ConcurrentBag<ActivityLog> nodes)
    {
        var nextNodeId = 0L;
        var mock = new Mock<IDeploymentLogWriter>();

        mock.Setup(x => x.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
            {
                var node = new ActivityLog
                {
                    Id = Interlocked.Increment(ref nextNodeId),
                    ServerTaskId = taskId,
                    ParentId = parentId,
                    Name = name,
                    NodeType = nodeType,
                    Status = status,
                    SortOrder = sortOrder,
                    StartedAt = DateTimeOffset.UtcNow
                };
                nodes.Add(node);
                return node;
            });

        mock.Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, long seq, ServerTaskLogCategory cat, string msg, string src, long? nodeId, DateTimeOffset? at, CancellationToken _) =>
            {
                logs.Add(new CapturedLog(cat, msg, src, nodeId));
            })
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.FlushAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.GetTreeByTaskIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog>());

        return mock;
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            ServerTaskId = 1001,
            Task = new ServerTaskEntity { Id = 1001 },
            Deployment = new Deployment { Id = 2001, EnvironmentId = 1, ChannelId = 1 },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Id = 3001, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            AllTargets = new List<Machine> { new() { Name = "target-1" } },
            AllTargetsContext = new List<DeploymentTargetContext>
            {
                new()
                {
                    Machine = new Machine { Name = "target-1" },
                    CommunicationStyle = CommunicationStyle.KubernetesAgent,
                    Transport = new StubTransport()
                }
            }
        };
    }

    private sealed class StubTransport : IDeploymentTransport
    {
        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper => null;
        public IExecutionStrategy Strategy => null;
        public IHealthCheckStrategy HealthChecker => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.Unspecified;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.Unspecified;
        public bool RequiresContextPreparationForPackagedPayload => false;
    }
}
