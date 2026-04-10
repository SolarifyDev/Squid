using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

public class ResumeCheckpointTests
{
    // ========== ResumeCheckpointPhase ==========

    [Fact]
    public async Task ResumePhase_NoCheckpoint_DoesNotSetResumeIndex()
    {
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService.Setup(s => s.LoadAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((DeploymentExecutionCheckpoint)null);

        var phase = new ResumeCheckpointPhase(checkpointService.Object);
        var ctx = CreateBaseContext();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.ResumeFromBatchIndex.ShouldBeNull();
        ctx.FailureEncountered.ShouldBeFalse();
    }

    [Fact]
    public async Task ResumePhase_WithCheckpoint_SetsResumeState()
    {
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService.Setup(s => s.LoadAsync(1001, It.IsAny<CancellationToken>())).ReturnsAsync(new DeploymentExecutionCheckpoint
        {
            ServerTaskId = 1001,
            LastCompletedBatchIndex = 2,
            FailureEncountered = true,
            OutputVariablesJson = JsonSerializer.Serialize(new List<VariableDto>
            {
                new() { Name = "Squid.Action.Step1.Result", Value = "42" }
            })
        });

        var phase = new ResumeCheckpointPhase(checkpointService.Object);
        var ctx = CreateBaseContext();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.ResumeFromBatchIndex.ShouldBe(2);
        ctx.FailureEncountered.ShouldBeTrue();
        ctx.RestoredOutputVariables.ShouldContain(v => v.Name == "Squid.Action.Step1.Result" && v.Value == "42");
    }

    [Fact]
    public async Task ResumePhase_WithCheckpoint_NullOutputVars_DoesNotCrash()
    {
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService.Setup(s => s.LoadAsync(1001, It.IsAny<CancellationToken>())).ReturnsAsync(new DeploymentExecutionCheckpoint
        {
            ServerTaskId = 1001,
            LastCompletedBatchIndex = 0,
            FailureEncountered = false,
            OutputVariablesJson = null
        });

        var phase = new ResumeCheckpointPhase(checkpointService.Object);
        var ctx = CreateBaseContext();

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.ResumeFromBatchIndex.ShouldBe(0);
        ctx.FailureEncountered.ShouldBeFalse();
    }

    [Fact]
    public void ResumePhase_Order_Is50()
    {
        var phase = new ResumeCheckpointPhase(new Mock<IDeploymentCheckpointService>().Object);

        phase.Order.ShouldBe(50);
    }

    // ========== ExecuteStepsPhase — Batch Skip ==========

    [Fact]
    public async Task ExecuteSteps_ResumeFromBatch1_SkipsFirstBatch()
    {
        var strategy = new RecordingStrategy();
        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, checkpointService.Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());

        var ctx = CreateBaseContext();
        ctx.ResumeFromBatchIndex = 0;
        ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTarget("target-1", "web", transport) };
        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Step1", 1, "web", MakeAction("Action1")),
            MakeStep("Step2", 2, "web", MakeAction("Action2"))
        };
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var executedActions = strategy.Requests.Select(r => r.ScriptBody).ToList();
        executedActions.ShouldNotContain(s => s.Contains("ACTION=Action1", StringComparison.Ordinal));
        executedActions.ShouldContain(s => s.Contains("ACTION=Action2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteSteps_NoResume_ExecutesAllBatches()
    {
        var strategy = new RecordingStrategy();
        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, checkpointService.Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTarget("target-1", "web", transport) };
        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Step1", 1, "web", MakeAction("Action1")),
            MakeStep("Step2", 2, "web", MakeAction("Action2"))
        };
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var executedActions = strategy.Requests.Select(r => r.ScriptBody).ToList();
        executedActions.ShouldContain(s => s.Contains("ACTION=Action1", StringComparison.Ordinal));
        executedActions.ShouldContain(s => s.Contains("ACTION=Action2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteSteps_PersistsCheckpointAfterEachBatch()
    {
        var strategy = new RecordingStrategy();
        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var savedCheckpoints = new List<DeploymentExecutionCheckpoint>();
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService.Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentExecutionCheckpoint, CancellationToken>((cp, _) => savedCheckpoints.Add(cp))
            .Returns(Task.CompletedTask);

        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, checkpointService.Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTarget("target-1", "web", transport) };
        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Step1", 1, "web", MakeAction("Action1")),
            MakeStep("Step2", 2, "web", MakeAction("Action2"))
        };
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        savedCheckpoints.Count.ShouldBe(2);
        savedCheckpoints[0].LastCompletedBatchIndex.ShouldBe(0);
        savedCheckpoints[1].LastCompletedBatchIndex.ShouldBe(1);
    }

    // ========== Helpers ==========

    private static (IDeploymentLifecycle Lifecycle, Mock<IDeploymentLogWriter> LogWriterMock) CreateLifecycle()
    {
        var nextNodeId = 0L;
        var logWriter = new Mock<IDeploymentLogWriter>();

        logWriter
            .Setup(x => x.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
                new ActivityLog { Id = Interlocked.Increment(ref nextNodeId), ServerTaskId = taskId, ParentId = parentId, Name = name, NodeType = nodeType, Status = status, SortOrder = sortOrder, StartedAt = DateTimeOffset.UtcNow });

        logWriter
            .Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
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

        return (lifecycle, logWriter);
    }

    private static IActionHandlerRegistry CreateRegistry()
    {
        var handler = new SimpleRunScriptHandler();
        return Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            ServerTaskId = 1001,
            Task = new ServerTaskEntity { Id = 1001 },
            Deployment = new Deployment { Id = 2001, EnvironmentId = 1, ChannelId = 1 },
            Release = new ReleaseEntity { Id = 3001, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>()
        };
    }

    private static DeploymentTargetContext MakeTarget(string name, string roles, IDeploymentTransport transport)
    {
        return new DeploymentTargetContext
        {
            Machine = new Machine { Name = name, Roles = JsonSerializer.Serialize(roles.Split(',', StringSplitOptions.TrimEntries)) },
            EndpointContext = new EndpointContext { EndpointJson = "{}" },
            Transport = transport,
            CommunicationStyle = transport.CommunicationStyle
        };
    }

    private static DeploymentStepDto MakeStep(string name, int order, string targetRoles, DeploymentActionDto action)
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = string.Empty,
            Condition = "Success",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { StepId = order, PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = targetRoles }
            },
            Actions = new List<DeploymentActionDto> { action }
        };
    }

    private static DeploymentActionDto MakeAction(string name)
    {
        return new DeploymentActionDto
        {
            Id = name.GetHashCode(),
            Name = name,
            ActionOrder = 1,
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
        public TestTransport(IExecutionStrategy strategy, IScriptContextWrapper scriptWrapper)
        {
            Strategy = strategy;
            ScriptWrapper = scriptWrapper;
        }

        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper { get; }
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.Unspecified;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.Unspecified;
        public bool RequiresContextPreparationForPackagedPayload => false;
    }

    private sealed class RecordingStrategy : IExecutionStrategy
    {
        public ConcurrentBag<ScriptExecutionRequest> Requests { get; } = new();

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
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
    }
}
