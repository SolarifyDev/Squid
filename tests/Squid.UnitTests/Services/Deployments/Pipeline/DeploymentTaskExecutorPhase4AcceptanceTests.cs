using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentTaskExecutorPhase4AcceptanceTests
{
    [Fact]
    public async Task CreateTaskActivityNode_UsesOctopusStyleTitle()
    {
        var createdNodes = new List<(DeploymentActivityLogNodeType NodeType, string Name)>();
        var (lifecycle, _) = CreateLifecycle((nodeType, name) => createdNodes.Add((nodeType, name)));
        var ctx = CreateBaseContext();

        ctx.Project = new Project { Name = "Smarties.Api" };
        ctx.Environment = new Squid.Core.Persistence.Entities.Deployments.Environment { Name = "TEST" };
        ctx.Release.Version = "6.2.5-mixture-v6.1-4016";

        lifecycle.Initialize(ctx);
        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);

        createdNodes.ShouldContain(x =>
            x.NodeType == DeploymentActivityLogNodeType.Task &&
            x.Name == "Deploy Smarties.Api release 6.2.5-mixture-v6.1-4016 to TEST");
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_DoesNotSeeOutputVariables_UntilNextBatch()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        var target = MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a");
        ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };
        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Step1", 1, null, "web", MakeAction("Action1")),
            MakeStep("Step2", 2, "StartWithPrevious", "web", MakeAction("Action2")),
            MakeStep("Step3", 3, null, "web", MakeAction("Action3"))
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var scripts = strategy.Requests.Select(r => r.ScriptBody).ToList();
        scripts.ShouldContain(s => s.Contains("ACTION=Action2", StringComparison.Ordinal) &&
                                   s.Contains("SEES_X=False", StringComparison.Ordinal));
        scripts.ShouldContain(s => s.Contains("ACTION=Action3", StringComparison.Ordinal) &&
                                   s.Contains("SEES_X=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_ParallelSteps_DoNotUseWrongTargetWrapperContext()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        var wrapper = new EndpointStampingWrapper();
        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, wrapper);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("machine-a", "role-a", transport, endpointJson: "endpoint-a"),
            MakeTarget("machine-b", "role-b", transport, endpointJson: "endpoint-b")
        };

        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("StepA", 1, null, "role-a", MakeAction("ActionA")),
            MakeStep("StepB", 2, "StartWithPrevious", "role-b", MakeAction("ActionB"))
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var requestsByMachine = strategy.Requests.ToDictionary(r => r.Machine.Name, r => r.ScriptBody);

        requestsByMachine["machine-a"].ShouldContain("WRAPPED_ENDPOINT=endpoint-a");
        requestsByMachine["machine-b"].ShouldContain("WRAPPED_ENDPOINT=endpoint-b");
        requestsByMachine["machine-a"].ShouldNotContain("endpoint-b");
        requestsByMachine["machine-b"].ShouldNotContain("endpoint-a");
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_Failure_DoesNotAffectSiblingStep_ButAffectsNextBatch()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        strategy.FailActions.Add("Action1");

        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a")
        };

        var step1 = MakeStep("Step1", 1, null, "web", MakeAction("Action1"));
        step1.IsRequired = false; // allow batch to complete and merge failure after join

        var step2 = MakeStep("Step2", 2, "StartWithPrevious", "web", MakeAction("Action2"));
        step2.Condition = "Success";

        var step3 = MakeStep("Step3", 3, null, "web", MakeAction("Action3"));
        step3.Condition = "Success";

        ctx.Steps = new List<DeploymentStepDto> { step1, step2, step3 };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var executedActions = strategy.Requests
            .Select(x => x.ScriptBody)
            .Where(x => x != null)
            .ToList();

        executedActions.ShouldContain(x => x.Contains("ACTION=Action1", StringComparison.Ordinal));
        executedActions.ShouldContain(x => x.Contains("ACTION=Action2", StringComparison.Ordinal));
        executedActions.ShouldNotContain(x => x.Contains("ACTION=Action3", StringComparison.Ordinal));
        ctx.FailureEncountered.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_UsesStepOrderAndWorkerNamingForActivityNodes()
    {
        var strategy = new RecordingStrategy();
        var handler = new CoordinatedRunScriptHandler(new AsyncBarrier(1));
        var createdNodes = new List<(DeploymentActivityLogNodeType NodeType, string Name)>();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle((nodeType, name) => createdNodes.Add((nodeType, name)));
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTarget("SJ-US-AKS", "web", transport, endpointJson: "endpoint-a") };
        ctx.Steps = new List<DeploymentStepDto> { MakeStep("Deploy web", 4, null, "web", MakeAction("ActionA")) };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        createdNodes.ShouldContain(x => x.NodeType == DeploymentActivityLogNodeType.Step && x.Name == "Step 1: Deploy web");
        createdNodes.ShouldContain(x => x.NodeType == DeploymentActivityLogNodeType.Action && x.Name == "Executing on SJ-US-AKS");
    }

    private static (IDeploymentLifecycle Lifecycle, Mock<IServerTaskService> ServerTaskServiceMock) CreateLifecycle(Action<DeploymentActivityLogNodeType, string> onAddActivityNode = null)
    {
        var nextNodeId = 0L;
        var serverTaskServiceMock = new Mock<IServerTaskService>();
        serverTaskServiceMock
            .Setup(x => x.AddActivityNodeAsync(
                It.IsAny<int>(),
                It.IsAny<long?>(),
                It.IsAny<string>(),
                It.IsAny<DeploymentActivityLogNodeType>(),
                It.IsAny<DeploymentActivityLogNodeStatus>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
            {
                onAddActivityNode?.Invoke(nodeType, name);

                return new Squid.Core.Persistence.Entities.Deployments.ActivityLog
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
            });
        serverTaskServiceMock
            .Setup(x => x.UpdateActivityNodeStatusAsync(
                It.IsAny<long>(),
                It.IsAny<DeploymentActivityLogNodeStatus>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        serverTaskServiceMock
            .Setup(x => x.AddLogAsync(
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<ServerTaskLogCategory>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        serverTaskServiceMock
            .Setup(x => x.AddLogsAsync(
                It.IsAny<int>(),
                It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new DeploymentActivityLogger(serverTaskServiceMock.Object, new Mock<IActivityLogDataProvider>().Object);
        var lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });

        return (lifecycle, serverTaskServiceMock);
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            Task = new ServerTaskEntity { Id = 1001 },
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
            SelectedPackages = new List<Squid.Core.Persistence.Entities.Deployments.ReleaseSelectedPackage>()
        };
    }

    private static DeploymentTargetContext MakeTarget(
        string name,
        string roles,
        IDeploymentTransport transport,
        string endpointJson)
    {
        return new DeploymentTargetContext
        {
            Machine = new Machine
            {
                Name = name,
                Roles = System.Text.Json.JsonSerializer.Serialize(new[] { roles })
            },
            EndpointContext = new EndpointContext { EndpointJson = endpointJson },
            Transport = transport,
            CommunicationStyle = transport.CommunicationStyle
        };
    }

    private static DeploymentStepDto MakeStep(
        string name,
        int order,
        string startTrigger,
        string targetRoles,
        DeploymentActionDto action)
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = startTrigger ?? string.Empty,
            Condition = "Success",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new()
                {
                    StepId = order,
                    PropertyName = DeploymentVariables.Action.TargetRoles,
                    PropertyValue = targetRoles
                }
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
            ActionType = "Squid.KubernetesRunScript",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentActionPropertyDto>()
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

    private sealed class EndpointStampingWrapper : IScriptContextWrapper
    {
        public string WrapScript(string script, ScriptContext context)
            => $"WRAPPED_ENDPOINT={context.Endpoint.EndpointJson};{script}";
    }

    private sealed class RecordingStrategy : IExecutionStrategy
    {
        public ConcurrentBag<ScriptExecutionRequest> Requests { get; } = new();
        public HashSet<string> FailActions { get; } = new(StringComparer.Ordinal);

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            var logs = new List<string>();
            if (request.ScriptBody.Contains("ACTION=Action1", StringComparison.Ordinal))
                logs.Add("##squid[setVariable name='X' value='1']");

            var shouldFail = FailActions.Any(actionName =>
                request.ScriptBody.Contains($"ACTION={actionName}", StringComparison.Ordinal));

            return Task.FromResult(new ScriptExecutionResult
            {
                Success = !shouldFail,
                ExitCode = shouldFail ? 1 : 0,
                LogLines = logs
            });
        }
    }

    private sealed class CoordinatedRunScriptHandler : IActionHandler
    {
        private readonly AsyncBarrier _barrier;

        public CoordinatedRunScriptHandler(AsyncBarrier barrier)
        {
            _barrier = barrier;
        }

        public DeploymentActionType ActionType => DeploymentActionType.KubernetesRunScript;

        public bool CanHandle(DeploymentActionDto action)
            => DeploymentActionTypeParser.Is(action?.ActionType, ActionType);

        public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            if (ctx.Action.Name is "Action1" or "Action2" or "ActionA" or "ActionB")
                await _barrier.SignalAndWaitAsync(ct).ConfigureAwait(false);

            var seesX = ctx.Variables?.Any(v => v.Name == "X") == true;

            return new ActionExecutionResult
            {
                ScriptBody = $"ACTION={ctx.Action.Name};SEES_X={seesX}",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                ContextPreparationPolicy = ContextPreparationPolicy.Apply
            };
        }
    }

    private sealed class AsyncBarrier
    {
        private readonly int _participants;
        private int _count;
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsyncBarrier(int participants)
        {
            _participants = participants;
        }

        public async Task SignalAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) >= _participants)
                _tcs.TrySetResult();

            using var _ = ct.Register(() => _tcs.TrySetCanceled(ct));
            await _tcs.Task.ConfigureAwait(false);
        }
    }
}
