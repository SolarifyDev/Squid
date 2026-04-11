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
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Acceptance tests for RunOnServer execution: verifies that RunOnServer steps
/// execute through the server transport, propagate failures and output variables,
/// and respect step conditions.
/// </summary>
public class RunOnServerExecutionTests
{
    private record CapturedLog(ServerTaskLogCategory Category, string Message, string Source, long? ActivityNodeId);

    // ========== Basic RunOnServer Execution ==========

    [Fact]
    public async Task RunOnServerStep_ExecutesThroughServerTransport()
    {
        var captured = new List<ScriptExecutionRequest>();
        var (phase, ctx, logs, _) = CreateRunOnServerTestHarness(new CapturingStrategy(captured));

        ctx.Steps = new List<DeploymentStepDto> { MakeRunOnServerStep("Server Step", 1) };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        captured.Count.ShouldBe(1);
        captured[0].Machine.Name.ShouldBe("Squid Server");
        captured[0].Machine.Id.ShouldBe(0);
    }

    [Fact]
    public async Task RunOnServerStep_EmitsRunOnServerExecutingEvent()
    {
        var (phase, ctx, logs, _) = CreateRunOnServerTestHarness();

        ctx.Steps = new List<DeploymentStepDto> { MakeRunOnServerStep("Server Step", 1) };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        logs.ShouldContain(l => l.Message.Contains("Executing step") && l.Message.Contains("Server Step") && l.Message.Contains("on server"));
    }

    // ========== Failure Propagation ==========

    [Fact]
    public async Task RunOnServerStep_Failure_SetsFailureEncountered()
    {
        var (phase, ctx, _, _) = CreateRunOnServerTestHarness(new FailingStrategy());

        var step = MakeRunOnServerStep("Failing Server Step", 1);
        step.IsRequired = false;
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.FailureEncountered.ShouldBeTrue();
    }

    [Fact]
    public async Task RunOnServerStep_Failure_SubsequentSuccessConditionStep_Skipped()
    {
        var captured = new List<ScriptExecutionRequest>();
        var (phase, ctx, logs, _) = CreateMixedTestHarness(new FailingStrategy(), new CapturingStrategy(captured));

        var serverStep = MakeRunOnServerStep("Failing Server Step", 1);
        serverStep.IsRequired = false;

        var targetStep = MakeTargetStep("Target Step", 2, "web", condition: "Success");
        targetStep.Actions = new List<DeploymentActionDto> { MakeAction("TargetAction") };

        ctx.Steps = new List<DeploymentStepDto> { serverStep, targetStep };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        captured.ShouldBeEmpty("Target step should be skipped after server step failure");
        logs.ShouldContain(l => l.Message.Contains("previous step failed"));
    }

    // ========== Condition Evaluation ==========

    [Fact]
    public async Task RunOnServerStep_FailureCondition_ExecutesAfterPriorFailure()
    {
        var captured = new List<ScriptExecutionRequest>();
        var (phase, ctx, _, _) = CreateRunOnServerTestHarness(new CapturingStrategy(captured));
        ctx.FailureEncountered = true;

        var step = MakeRunOnServerStep("Cleanup Step", 1, condition: "Failure");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        captured.Count.ShouldBe(1, "RunOnServer step with Condition=Failure should execute when prior failure exists");
    }

    [Fact]
    public async Task RunOnServerStep_FailureCondition_SkippedWhenNoPriorFailure()
    {
        var captured = new List<ScriptExecutionRequest>();
        var (phase, ctx, _, _) = CreateRunOnServerTestHarness(new CapturingStrategy(captured));
        ctx.FailureEncountered = false;

        var step = MakeRunOnServerStep("Cleanup Step", 1, condition: "Failure");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        captured.ShouldBeEmpty("RunOnServer step with Condition=Failure should be skipped when no prior failure");
    }

    [Fact]
    public async Task RunOnServerStep_AlwaysCondition_ExecutesRegardlessOfFailure()
    {
        var captured = new List<ScriptExecutionRequest>();
        var (phase, ctx, _, _) = CreateRunOnServerTestHarness(new CapturingStrategy(captured));
        ctx.FailureEncountered = true;

        var step = MakeRunOnServerStep("Always Step", 1, condition: "Always");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        captured.Count.ShouldBe(1, "RunOnServer step with Condition=Always should execute after failure");
    }

    // ========== Output Variable Propagation ==========

    [Fact]
    public async Task RunOnServerStep_OutputVariables_PropagatedToContext()
    {
        var outputLines = new List<string> { "##squid[setVariable name='DeployResult' value='deployed-v2']" };
        var (phase, ctx, _, _) = CreateRunOnServerTestHarness(new ScriptOutputStrategy(outputLines));

        ctx.Steps = new List<DeploymentStepDto> { MakeRunOnServerStep("Server Deploy", 1) };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.Variables.ShouldContain(v => v.Name == "DeployResult" && v.Value == "deployed-v2");
        ctx.Variables.ShouldContain(v => v.Name == SpecialVariables.Output.Variable("Server Deploy", "DeployResult") && v.Value == "deployed-v2");
    }

    [Fact]
    public async Task RunOnServerStep_OutputVariables_AvailableToSubsequentStep()
    {
        var outputLines = new List<string> { "##squid[setVariable name='ArtifactPath' value='/tmp/build.zip']" };
        var targetCaptured = new List<ScriptExecutionRequest>();
        var (phase, ctx, _, _) = CreateMixedTestHarness(new ScriptOutputStrategy(outputLines), new CapturingStrategy(targetCaptured));

        var serverStep = MakeRunOnServerStep("Build Step", 1);

        var targetStep = MakeTargetStep("Deploy Step", 2, "web");
        targetStep.Actions = new List<DeploymentActionDto> { MakeAction("DeployAction") };

        ctx.Steps = new List<DeploymentStepDto> { serverStep, targetStep };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        targetCaptured.Count.ShouldBe(1);

        var targetVars = targetCaptured[0].Variables;
        targetVars.ShouldContain(v => v.Name == "ArtifactPath" && v.Value == "/tmp/build.zip");
    }

    // ========== Multiple Sequential RunOnServer Steps ==========

    [Fact]
    public async Task MultipleRunOnServerSteps_AllExecuteSequentially()
    {
        var captured = new List<ScriptExecutionRequest>();
        var (phase, ctx, _, _) = CreateRunOnServerTestHarness(new CapturingStrategy(captured));

        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeRunOnServerStep("Step 1", 1),
            MakeRunOnServerStep("Step 2", 2),
            MakeRunOnServerStep("Step 3", 3)
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        captured.Count.ShouldBe(3);
        captured.ShouldAllBe(r => r.Machine.Name == "Squid Server");
    }

    // ========== Mixed Deployment ==========

    [Fact]
    public async Task MixedDeployment_RunOnServerAndTarget_BothExecute()
    {
        var serverCaptured = new List<ScriptExecutionRequest>();
        var targetCaptured = new List<ScriptExecutionRequest>();
        var (phase, ctx, _, _) = CreateMixedTestHarness(new CapturingStrategy(serverCaptured), new CapturingStrategy(targetCaptured));

        var serverStep = MakeRunOnServerStep("Server Step", 1);

        var targetStep = MakeTargetStep("Target Step", 2, "web");
        targetStep.Actions = new List<DeploymentActionDto> { MakeAction("TargetAction") };

        ctx.Steps = new List<DeploymentStepDto> { serverStep, targetStep };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        serverCaptured.Count.ShouldBe(1);
        serverCaptured[0].Machine.Name.ShouldBe("Squid Server");

        targetCaptured.Count.ShouldBe(1);
        targetCaptured[0].Machine.Name.ShouldBe("target-1");
    }

    // ========== Disabled RunOnServer Step ==========

    [Fact]
    public async Task RunOnServerStep_Disabled_Skipped()
    {
        var captured = new List<ScriptExecutionRequest>();
        var (phase, ctx, _, _) = CreateRunOnServerTestHarness(new CapturingStrategy(captured));

        var step = MakeRunOnServerStep("Disabled Step", 1);
        step.IsDisabled = true;
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        captured.ShouldBeEmpty();
    }

    // ========== Test Infrastructure ==========

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateRunOnServerTestHarness(IExecutionStrategy serverStrategy = null)
    {
        serverStrategy ??= new SuccessStrategy();

        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var serverTransport = new TestTransport(serverStrategy, CommunicationStyle.None);
        var transportRegistryMock = new Mock<ITransportRegistry>();
        transportRegistryMock.Setup(r => r.Resolve(CommunicationStyle.None)).Returns(serverTransport);

        var lifecycle = CreateLifecycle(logs, nodes);
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, transportRegistryMock.Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>();

        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateMixedTestHarness(IExecutionStrategy serverStrategy, IExecutionStrategy targetStrategy)
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var serverTransport = new TestTransport(serverStrategy, CommunicationStyle.None);
        var targetTransport = new TestTransport(targetStrategy, CommunicationStyle.KubernetesAgent);

        var transportRegistryMock = new Mock<ITransportRegistry>();
        transportRegistryMock.Setup(r => r.Resolve(CommunicationStyle.None)).Returns(serverTransport);

        var lifecycle = CreateLifecycle(logs, nodes);
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, transportRegistryMock.Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", targetTransport)
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

    private static DeploymentStepDto MakeRunOnServerStep(string name, int order, string condition = "Success")
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = string.Empty,
            Condition = condition,
            IsRequired = true,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { StepId = order, PropertyName = SpecialVariables.Step.RunOnServer, PropertyValue = "true" }
            },
            Actions = new List<DeploymentActionDto> { MakeAction($"{name}-Action") }
        };
    }

    private static DeploymentStepDto MakeTargetStep(string name, int order, string targetRoles, string condition = "Success")
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = string.Empty,
            Condition = condition,
            IsRequired = true,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { StepId = order, PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = targetRoles }
            },
            Actions = new List<DeploymentActionDto>()
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
        public TestTransport(IExecutionStrategy strategy, CommunicationStyle communicationStyle)
        {
            Strategy = strategy;
            CommunicationStyle = communicationStyle;
        }

        public CommunicationStyle CommunicationStyle { get; }
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper => null;
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.ApiWorkerLocal;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.LocalProcess;
        public bool RequiresContextPreparationForPackagedPayload => false;
    }

    private sealed class SuccessStrategy : IExecutionStrategy
    {
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
        }
    }

    private sealed class CapturingStrategy : IExecutionStrategy
    {
        private readonly List<ScriptExecutionRequest> _captured;

        public CapturingStrategy(List<ScriptExecutionRequest> captured) => _captured = captured;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            _captured.Add(request);

            return Task.FromResult(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
        }
    }

    private sealed class ScriptOutputStrategy : IExecutionStrategy
    {
        private readonly List<string> _lines;

        public ScriptOutputStrategy(List<string> lines) => _lines = lines;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = _lines });
        }
    }

    private sealed class FailingStrategy : IExecutionStrategy
    {
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "Error: script failed" } });
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
                ContextPreparationPolicy = ContextPreparationPolicy.Skip
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
