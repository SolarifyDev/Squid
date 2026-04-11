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
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Tests that multi-target parallel execution produces correct activity node
/// structure and log scoping. Validates the fix for concurrent DbContext access
/// through serialized lifecycle event emission.
/// </summary>
public class DeploymentMultiTargetLoggingTests
{
    private record CapturedLog(ServerTaskLogCategory Category, string Message, string Source, long? ActivityNodeId);

    // ========== Multi-Target Node Structure ==========

    [Fact]
    public async Task TwoTargets_OneStep_EachTargetGetsOwnActionNode()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var actionNodes = nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action).ToList();
        actionNodes.Count.ShouldBe(2, "Each target should have its own action node");

        actionNodes.ShouldContain(n => n.Name.Contains("Target-A", StringComparison.Ordinal));
        actionNodes.ShouldContain(n => n.Name.Contains("Target-B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoTargets_ActionNodesAreChildrenOfStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var actionNodes = nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action).ToList();

        foreach (var actionNode in actionNodes)
            actionNode.ParentId.ShouldBe(stepNode.Id, $"Action node '{actionNode.Name}' should be child of step node");
    }

    // ========== Script Output Scoping ==========

    [Fact]
    public async Task TwoTargets_ScriptOutputScopedToCorrectActionNode()
    {
        var strategyA = new ScriptOutputStrategy(new List<string> { "output-from-A" });
        var strategyB = new ScriptOutputStrategy(new List<string> { "output-from-B" });
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness(
            ("Target-A", strategyA),
            ("Target-B", strategyB));

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var nodeA = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Target-A", StringComparison.Ordinal));
        var nodeB = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Target-B", StringComparison.Ordinal));
        nodeA.ShouldNotBeNull();
        nodeB.ShouldNotBeNull();

        var logA = logs.FirstOrDefault(l => l.Message.Contains("output-from-A", StringComparison.Ordinal));
        logA.ShouldNotBeNull("Script output from Target-A should be captured");
        logA.ActivityNodeId.ShouldBe(nodeA.Id, "Target-A output should be scoped to Target-A action node");

        var logB = logs.FirstOrDefault(l => l.Message.Contains("output-from-B", StringComparison.Ordinal));
        logB.ShouldNotBeNull("Script output from Target-B should be captured");
        logB.ActivityNodeId.ShouldBe(nodeB.Id, "Target-B output should be scoped to Target-B action node");
    }

    [Fact]
    public async Task TwoTargets_NoScriptOutputLandsOnTaskRootNode()
    {
        var strategyA = new ScriptOutputStrategy(new List<string> { "line-A-1", "line-A-2" });
        var strategyB = new ScriptOutputStrategy(new List<string> { "line-B-1", "line-B-2" });
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness(
            ("Target-A", strategyA),
            ("Target-B", strategyB));

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var taskNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Task);
        var actionNodes = nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action).Select(n => n.Id).ToHashSet();

        var scriptLogs = logs.Where(l => l.Message.StartsWith("line-", StringComparison.Ordinal)).ToList();
        scriptLogs.ShouldNotBeEmpty("Should have script output logs");

        foreach (var log in scriptLogs)
        {
            log.ActivityNodeId.ShouldNotBeNull("Script output should have an ActivityNodeId");
            actionNodes.ShouldContain(log.ActivityNodeId.Value, "Script output should be scoped to an action node, not the task root");
        }
    }

    // ========== Success/Failure Message Scoping ==========

    [Fact]
    public async Task TwoTargets_SuccessMessagesScopedToCorrectActionNode()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var nodeA = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Target-A", StringComparison.Ordinal));
        var nodeB = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Target-B", StringComparison.Ordinal));

        var successA = logs.FirstOrDefault(l => l.Message.Contains("Target-A", StringComparison.Ordinal) && l.Message.Contains("Successfully finished", StringComparison.OrdinalIgnoreCase));
        successA.ShouldNotBeNull();
        successA.ActivityNodeId.ShouldBe(nodeA.Id);

        var successB = logs.FirstOrDefault(l => l.Message.Contains("Target-B", StringComparison.Ordinal) && l.Message.Contains("Successfully finished", StringComparison.OrdinalIgnoreCase));
        successB.ShouldNotBeNull();
        successB.ActivityNodeId.ShouldBe(nodeB.Id);
    }

    [Fact]
    public async Task TwoTargets_OneFailsOneFails_ErrorMessagesScopedCorrectly()
    {
        var successStrategy = new SuccessStrategy();
        var failStrategy = new FailingStrategy();
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness(
            ("Target-OK", successStrategy),
            ("Target-Fail", failStrategy));

        var step = MakeStep("Deploy web", 1, "web");
        step.IsRequired = false;
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var nodeOK = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Target-OK", StringComparison.Ordinal));
        var nodeFail = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Target-Fail", StringComparison.Ordinal));
        nodeOK.ShouldNotBeNull();
        nodeFail.ShouldNotBeNull();

        var successLog = logs.FirstOrDefault(l => l.Message.Contains("Successfully finished", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("Target-OK", StringComparison.Ordinal));
        successLog.ShouldNotBeNull();
        successLog.ActivityNodeId.ShouldBe(nodeOK.Id);

        var errorLog = logs.FirstOrDefault(l => l.Category == ServerTaskLogCategory.Error && l.ActivityNodeId == nodeFail.Id);
        errorLog.ShouldNotBeNull("Error should be scoped to failing target's action node");
    }

    // ========== Multiple Actions per Step ==========

    [Fact]
    public async Task TwoTargets_TwoActions_FourActionNodes()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto>
        {
            MakeAction("Action1", actionOrder: 1),
            MakeAction("Action2", actionOrder: 2)
        };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var actionNodes = nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action).ToList();
        actionNodes.Count.ShouldBe(4, "2 targets × 2 actions = 4 action nodes");

        var targetANodes = actionNodes.Where(n => n.Name.Contains("Target-A", StringComparison.Ordinal)).ToList();
        var targetBNodes = actionNodes.Where(n => n.Name.Contains("Target-B", StringComparison.Ordinal)).ToList();
        targetANodes.Count.ShouldBe(2);
        targetBNodes.Count.ShouldBe(2);
    }

    // ========== Multiple Steps ==========

    [Fact]
    public async Task TwoTargets_TwoSteps_EachStepHasItsOwnTargetActionNodes()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step1 = MakeStep("Deploy web", 1, "web");
        step1.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };

        var step2 = MakeStep("Deploy config", 2, "web");
        step2.Actions = new List<DeploymentActionDto> { MakeAction("Deploy config") };

        ctx.Steps = new List<DeploymentStepDto> { step1, step2 };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNodes = nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Step).ToList();
        stepNodes.Count.ShouldBe(2, "Two steps should produce two step nodes");

        var actionNodes = nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action).ToList();
        actionNodes.Count.ShouldBe(4, "2 steps × 2 targets = 4 action nodes");

        foreach (var stepNode in stepNodes)
        {
            var childActions = actionNodes.Where(n => n.ParentId == stepNode.Id).ToList();
            childActions.Count.ShouldBe(2, $"Step '{stepNode.Name}' should have 2 action child nodes (one per target)");
        }
    }

    // ========== ActionRunning Includes Machine Name ==========

    [Fact]
    public async Task TwoTargets_ActionRunningLogIncludesMachineName()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var runningLogs = logs.Where(l => l.Message.Contains("Running action", StringComparison.OrdinalIgnoreCase)).ToList();
        runningLogs.Count.ShouldBe(2, "Should have one 'Running action' log per target");

        runningLogs.ShouldContain(l => l.Message.Contains("Target-A", StringComparison.Ordinal), "Running action log should mention Target-A");
        runningLogs.ShouldContain(l => l.Message.Contains("Target-B", StringComparison.Ordinal), "Running action log should mention Target-B");
    }

    // ========== Three Targets — Verifies Beyond Two ==========

    [Fact]
    public async Task ThreeTargets_AllActionNodesCreatedAndScriptOutputScoped()
    {
        var strategyA = new ScriptOutputStrategy(new List<string> { "from-A" });
        var strategyB = new ScriptOutputStrategy(new List<string> { "from-B" });
        var strategyC = new ScriptOutputStrategy(new List<string> { "from-C" });
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness(
            ("Target-A", strategyA),
            ("Target-B", strategyB),
            ("Target-C", strategyC));

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var actionNodes = nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Action).ToList();
        actionNodes.Count.ShouldBe(3);

        foreach (var targetName in new[] { "Target-A", "Target-B", "Target-C" })
        {
            var node = actionNodes.FirstOrDefault(n => n.Name.Contains(targetName, StringComparison.Ordinal));
            node.ShouldNotBeNull($"Action node for {targetName} should exist");

            var marker = $"from-{targetName.Last()}";
            var scriptLog = logs.FirstOrDefault(l => l.Message.Contains(marker, StringComparison.Ordinal));
            scriptLog.ShouldNotBeNull($"Script output '{marker}' should exist");
            scriptLog.ActivityNodeId.ShouldBe(node.Id, $"Script output '{marker}' should be scoped to {targetName}");
        }
    }

    // ========== Concurrent Targets with Delayed Strategy ==========

    [Fact]
    public async Task ConcurrentTargets_WithDelay_ScriptOutputNeverLandsOnWrongNode()
    {
        var strategyA = new DelayedScriptOutputStrategy(50, new List<string> { "slow-A-1", "slow-A-2", "slow-A-3" });
        var strategyB = new DelayedScriptOutputStrategy(10, new List<string> { "fast-B-1", "fast-B-2" });
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness(
            ("Slow-Target", strategyA),
            ("Fast-Target", strategyB));

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var nodeSlow = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Slow-Target", StringComparison.Ordinal));
        var nodeFast = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action && n.Name.Contains("Fast-Target", StringComparison.Ordinal));
        nodeSlow.ShouldNotBeNull();
        nodeFast.ShouldNotBeNull();

        var slowLogs = logs.Where(l => l.Message.Contains("slow-A-", StringComparison.Ordinal)).ToList();
        slowLogs.Count.ShouldBe(3);

        foreach (var log in slowLogs)
            log.ActivityNodeId.ShouldBe(nodeSlow.Id, $"'{log.Message}' should be on Slow-Target node");

        var fastLogs = logs.Where(l => l.Message.Contains("fast-B-", StringComparison.Ordinal)).ToList();
        fastLogs.Count.ShouldBe(2);

        foreach (var log in fastLogs)
            log.ActivityNodeId.ShouldBe(nodeFast.Id, $"'{log.Message}' should be on Fast-Target node");
    }

    // ========== Sequence Number Uniqueness ==========

    [Fact]
    public async Task ConcurrentTargets_SequenceNumbersAreUnique()
    {
        var capturedSequences = new ConcurrentBag<long>();
        var (phase, ctx, _, _) = CreateMultiTargetHarnessWithSeqCapture("Target-A", "Target-B", capturedSequences);

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var sequences = capturedSequences.ToList();
        sequences.Count.ShouldBeGreaterThan(0, "Should have captured sequence numbers");
        sequences.Distinct().Count().ShouldBe(sequences.Count, "All sequence numbers should be unique");
    }

    // ========== Step Completion Logged Once ==========

    [Fact]
    public async Task TwoTargets_StepCompletedLoggedOnce()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var completedLogs = logs.Where(l => l.Message.Contains("completed successfully", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("Deploy web", StringComparison.Ordinal)).ToList();
        completedLogs.Count.ShouldBe(1, "Step completed should be logged exactly once regardless of target count");
    }

    // ========== ExecutingOnTarget Logged Per Target Under Step Node ==========

    [Fact]
    public async Task TwoTargets_ExecutingOnTargetLoggedPerTargetUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateMultiTargetHarness("Target-A", "Target-B");

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("Deploy web") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var execLogs = logs.Where(l => l.Message.Contains("Executing step", StringComparison.OrdinalIgnoreCase)).ToList();
        execLogs.Count.ShouldBe(2, "Should log 'Executing step' once per target");

        foreach (var log in execLogs)
            log.ActivityNodeId.ShouldBe(stepNode.Id, "'Executing step' log should be under step node");

        execLogs.ShouldContain(l => l.Message.Contains("Target-A", StringComparison.Ordinal));
        execLogs.ShouldContain(l => l.Message.Contains("Target-B", StringComparison.Ordinal));
    }

    // ========== Test Infrastructure ==========

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateMultiTargetHarness(params string[] targetNames)
    {
        var strategy = new SuccessStrategy();
        var targets = targetNames.Select(name => (name, (IExecutionStrategy)strategy)).ToArray();
        return CreateMultiTargetHarness(targets);
    }

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateMultiTargetHarness(params (string Name, IExecutionStrategy Strategy)[] targets)
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var lifecycle = CreateLifecycle(logs, nodes);
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = targets.Select(t => MakeTarget(t.Name, "web", new TestTransport(t.Strategy))).ToList();
        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateMultiTargetHarnessWithSeqCapture(string targetA, string targetB, ConcurrentBag<long> capturedSequences)
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var lifecycle = CreateLifecycleWithSeqCapture(logs, nodes, capturedSequences);
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());

        var ctx = CreateBaseContext();
        var strategy = new SuccessStrategy();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget(targetA, "web", new TestTransport(strategy)),
            MakeTarget(targetB, "web", new TestTransport(strategy))
        };
        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static IDeploymentLifecycle CreateLifecycle(ConcurrentBag<CapturedLog> logs, ConcurrentBag<ActivityLog> nodes)
    {
        var logWriter = CreateLogWriterMock(logs, nodes);
        var logger = new DeploymentActivityLogger(logWriter.Object);
        return new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });
    }

    private static IDeploymentLifecycle CreateLifecycleWithSeqCapture(ConcurrentBag<CapturedLog> logs, ConcurrentBag<ActivityLog> nodes, ConcurrentBag<long> capturedSequences)
    {
        var logWriter = CreateLogWriterMock(logs, nodes);

        logWriter
            .Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, long seq, ServerTaskLogCategory cat, string msg, string src, long? nodeId, DateTimeOffset? at, CancellationToken _) =>
            {
                capturedSequences.Add(seq);
                logs.Add(new CapturedLog(cat, msg, src, nodeId));
            })
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken _) =>
            {
                foreach (var entry in entries)
                {
                    capturedSequences.Add(entry.SequenceNumber);
                    logs.Add(new CapturedLog(entry.Category, entry.MessageText, entry.Source, entry.ActivityNodeId));
                }
            })
            .Returns(Task.CompletedTask);

        var logger = new DeploymentActivityLogger(logWriter.Object);
        return new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });
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
            .Callback((int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken _) =>
            {
                foreach (var entry in entries)
                    logs.Add(new CapturedLog(entry.Category, entry.MessageText, entry.Source, entry.ActivityNodeId));
            })
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
            Task = new Squid.Core.Persistence.Entities.Deployments.ServerTask { Id = 1001 },
            Deployment = new Deployment
            {
                Id = 2001,
                EnvironmentId = 1,
                ChannelId = 1
            },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release
            {
                Id = 3001,
                Version = "1.0.0"
            },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>()
        };
    }

    private static DeploymentTargetContext MakeTarget(string name, string roles, IDeploymentTransport transport)
    {
        return new DeploymentTargetContext
        {
            Machine = new Machine
            {
                Name = name,
                Roles = System.Text.Json.JsonSerializer.Serialize(roles.Split(',', StringSplitOptions.TrimEntries))
            },
            EndpointContext = new EndpointContext { EndpointJson = "{}" },
            Transport = transport,
            CommunicationStyle = transport.CommunicationStyle
        };
    }

    private static DeploymentStepDto MakeStep(string name, int order, string targetRoles, bool isDisabled = false, string condition = "Success")
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = string.Empty,
            Condition = condition,
            IsRequired = true,
            IsDisabled = isDisabled,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { StepId = order, PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = targetRoles }
            },
            Actions = new List<DeploymentActionDto>()
        };
    }

    private static DeploymentActionDto MakeAction(string name, int actionOrder = 1)
    {
        return new DeploymentActionDto
        {
            Id = name.GetHashCode(),
            Name = name,
            ActionOrder = actionOrder,
            ActionType = "Squid.Script",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentActionPropertyDto>(),
            Environments = new List<int>(),
            ExcludedEnvironments = new List<int>(),
            Channels = new List<int>()
        };
    }

    private sealed class TestTransport : IDeploymentTransport
    {
        public TestTransport(IExecutionStrategy strategy)
        {
            Strategy = strategy;
        }

        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ITransportCapabilities Capabilities { get; } = new TransportCapabilities();
    }

    private sealed class SuccessStrategy : IExecutionStrategy
    {
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = new List<string>()
            });
        }
    }

    private sealed class ScriptOutputStrategy : IExecutionStrategy
    {
        private readonly List<string> _lines;

        public ScriptOutputStrategy(List<string> lines) => _lines = lines;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = _lines
            });
        }
    }

    private sealed class DelayedScriptOutputStrategy : IExecutionStrategy
    {
        private readonly int _delayMs;
        private readonly List<string> _lines;

        public DelayedScriptOutputStrategy(int delayMs, List<string> lines)
        {
            _delayMs = delayMs;
            _lines = lines;
        }

        public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            await Task.Delay(_delayMs, ct).ConfigureAwait(false);

            return new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = _lines
            };
        }
    }

    private sealed class FailingStrategy : IExecutionStrategy
    {
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult
            {
                Success = false,
                ExitCode = 1,
                LogLines = new List<string> { "Error: script failed" }
            });
        }
    }

    private sealed class SimpleRunScriptHandler : IActionHandler
    {
        public string ActionType => "Squid.Script";

        public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            return Task.FromResult(new ActionExecutionResult
            {
                ScriptBody = $"echo ACTION={ctx.Action.Name}",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                ContextPreparationPolicy = ContextPreparationPolicy.Apply
            });
        }

        public Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct) =>
            Task.FromResult<ExecutionIntent>(new RunScriptIntent
            {
                Name = "run-script",
                ScriptBody = $"echo ACTION={ctx.Action.Name}",
                Syntax = ScriptSyntax.Bash
            });
    }
}
