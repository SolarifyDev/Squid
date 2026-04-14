using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// Phase 6c-iii — proves the executor's <c>PrepareStepActionsAsync</c> skips actions
/// whose <see cref="PlannedTargetDispatch.Validation"/> is invalid, preventing
/// unsupported (action × target) pairs from reaching the renderer/strategy. The
/// lifecycle emits <see cref="ActionCapabilityFilteredEvent"/> for each skip.
/// </summary>
public class ExecuteStepsPhaseCapabilityFilterTests : IDisposable
{
    private readonly Mock<IDeploymentLifecycle> _lifecycleMock;
    private readonly Mock<IExternalFeedDataProvider> _feedProviderMock;
    private readonly Mock<IPackageAcquisitionService> _acquisitionMock;
    private readonly Mock<IActionHandlerRegistry> _handlerRegistryMock;
    private readonly Mock<IDeploymentInterruptionService> _interruptionMock;
    private readonly Mock<IDeploymentCheckpointService> _checkpointMock;
    private readonly Mock<IServerTaskService> _taskServiceMock;
    private readonly Mock<ITransportRegistry> _transportRegistryMock;
    private readonly Mock<IExecutionStrategy> _strategyMock;
    private readonly Mock<IDeploymentTransport> _transportMock;
    private readonly ExecuteStepsPhase _phase;
    private readonly DeploymentTaskContext _ctx;
    private readonly CapturingProbeRenderer _sshRenderer;
    private readonly CapturingProbeRenderer _k8sRenderer;
    private readonly List<DeploymentLifecycleEvent> _emittedEvents;

    public ExecuteStepsPhaseCapabilityFilterTests()
    {
        _emittedEvents = new List<DeploymentLifecycleEvent>();
        _sshRenderer = new CapturingProbeRenderer(CommunicationStyle.Ssh);
        _k8sRenderer = new CapturingProbeRenderer(CommunicationStyle.KubernetesApi);

        _lifecycleMock = new Mock<IDeploymentLifecycle>();
        _lifecycleMock
            .Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentLifecycleEvent, CancellationToken>((e, _) => _emittedEvents.Add(e))
            .Returns(Task.CompletedTask);

        _feedProviderMock = new Mock<IExternalFeedDataProvider>();
        _acquisitionMock = new Mock<IPackageAcquisitionService>();
        _handlerRegistryMock = new Mock<IActionHandlerRegistry>();
        _interruptionMock = new Mock<IDeploymentInterruptionService>();
        _checkpointMock = new Mock<IDeploymentCheckpointService>();
        _taskServiceMock = new Mock<IServerTaskService>();
        _transportRegistryMock = new Mock<ITransportRegistry>();
        _strategyMock = new Mock<IExecutionStrategy>();
        _transportMock = new Mock<IDeploymentTransport>();

        _strategyMock
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });

        _transportMock.Setup(t => t.Strategy).Returns(_strategyMock.Object);
        _transportRegistryMock.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(_transportMock.Object);

        var rendererRegistry = new IntentRendererRegistry(new IIntentRenderer[] { _sshRenderer, _k8sRenderer });

        _phase = new ExecuteStepsPhase(
            _handlerRegistryMock.Object,
            _lifecycleMock.Object,
            _interruptionMock.Object,
            _checkpointMock.Object,
            _taskServiceMock.Object,
            _transportRegistryMock.Object,
            _feedProviderMock.Object,
            _acquisitionMock.Object,
            new ServiceMessageParser(),
            rendererRegistry);

        _ctx = new DeploymentTaskContext
        {
            ServerTaskId = 1,
            Deployment = new Deployment { Id = 1, EnvironmentId = 1, ChannelId = 1 },
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>(),
            Variables = new List<VariableDto>(),
            AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>(),
            AllTargetsContext = new List<DeploymentTargetContext>()
        };
    }

    public void Dispose() { }

    // ------------------------------------------------------------------
    // Shared fakes
    // ------------------------------------------------------------------

    private sealed class TrackingHandler : IActionHandler
    {
        public string ActionType { get; init; }
        public int DescribeIntentCallCount { get; private set; }

        public Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            DescribeIntentCallCount++;
            return Task.FromResult<ExecutionIntent>(new RunScriptIntent
            {
                Name = "test-intent",
                StepName = ctx.Step?.Name ?? string.Empty,
                ActionName = ctx.Action?.Name ?? string.Empty,
                ScriptBody = "echo test",
                Syntax = ScriptSyntax.Bash,
                InjectRuntimeBundle = false
            });
        }
    }

    private sealed class CapturingProbeRenderer : IIntentRenderer
    {
        public CapturingProbeRenderer(CommunicationStyle style) => CommunicationStyle = style;

        public CommunicationStyle CommunicationStyle { get; }
        public List<ExecutionIntent> CapturedIntents { get; } = new();

        public bool CanRender(ExecutionIntent intent) => intent is not null;

        public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
        {
            CapturedIntents.Add(intent);
            return Task.FromResult(new ScriptExecutionRequest
            {
                ScriptBody = intent is RunScriptIntent rs ? rs.ScriptBody : "rendered",
                Variables = context.EffectiveVariables.ToList(),
                Machine = context.Target.Machine,
                EndpointContext = context.Target.EndpointContext
            });
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private TrackingHandler InstallHandler(string actionType = null)
    {
        var handler = new TrackingHandler { ActionType = actionType ?? SpecialVariables.ActionTypes.Script };
        _handlerRegistryMock.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.TargetLevel);
        _handlerRegistryMock.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(handler);
        return handler;
    }

    private static DeploymentStepDto BuildStep(int id, string name, params DeploymentActionDto[] actions) => new()
    {
        Id = id,
        StepOrder = 1,
        Name = name,
        StepType = "Action",
        Condition = "Success",
        StartTrigger = "StartAfterPrevious",
        Properties = new List<DeploymentStepPropertyDto>(),
        Actions = actions.ToList()
    };

    private static DeploymentActionDto BuildAction(int id, string name, string actionType) => new()
    {
        Id = id,
        ActionOrder = id,
        Name = name,
        ActionType = actionType,
        IsDisabled = false,
        Properties = new List<DeploymentActionPropertyDto>()
    };

    private DeploymentTargetContext BuildTarget(int machineId, string name, CommunicationStyle style = CommunicationStyle.Ssh) => new()
    {
        Machine = new Machine { Id = machineId, Name = name, Roles = string.Empty },
        CommunicationStyle = style,
        Transport = _transportMock.Object,
        EndpointContext = new EndpointContext()
    };

    private static PlannedStep BuildPlannedStep(int stepId, params PlannedAction[] actions) => new()
    {
        StepId = stepId,
        StepName = "planned",
        StepOrder = 1,
        Status = PlannedStepStatus.Applicable,
        Actions = actions,
        MatchedTargets = actions.SelectMany(a => a.Dispatches).Select(d => d.Target).Distinct().ToArray()
    };

    private static PlannedAction BuildPlannedAction(int actionId, string actionType, params PlannedTargetDispatch[] dispatches) => new()
    {
        ActionId = actionId,
        ActionName = "test-action",
        ActionType = actionType,
        Dispatches = dispatches
    };

    private static PlannedTargetDispatch BuildDispatch(int machineId, string machineName, bool isValid, string violationMessage = null) => new()
    {
        Target = new PlannedTarget { MachineId = machineId, MachineName = machineName },
        Intent = new RunScriptIntent { Name = "test", ScriptBody = "echo 1", Syntax = ScriptSyntax.Bash },
        Validation = isValid
            ? CapabilityValidationResult.Supported
            : new CapabilityValidationResult
            {
                Violations = new[]
                {
                    new CapabilityViolation
                    {
                        Code = ViolationCodes.UnsupportedActionType,
                        Message = violationMessage ?? "transport does not support this action type"
                    }
                }
            }
    };

    private void SeedPlan(params PlannedStep[] steps)
    {
        _ctx.Plan = new DeploymentPlan
        {
            Mode = PlanMode.Preview,
            ReleaseId = 1,
            EnvironmentId = 1,
            DeploymentProcessSnapshotId = 1,
            Steps = steps
        };
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Action_SkippedWhenDispatchValidationFails()
    {
        var handler = InstallHandler();
        var action = BuildAction(1, "Helm Upgrade", "Squid.HelmChartUpgrade");
        var target = BuildTarget(1, "ssh-target");

        _ctx.Steps = new List<DeploymentStepDto> { BuildStep(10, "Deploy", action) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };

        SeedPlan(BuildPlannedStep(10,
            BuildPlannedAction(1, "Squid.HelmChartUpgrade",
                BuildDispatch(1, "ssh-target", isValid: false, "transport does not support action type 'Squid.HelmChartUpgrade'"))));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        handler.DescribeIntentCallCount.ShouldBe(0);
        _sshRenderer.CapturedIntents.ShouldBeEmpty();
        _emittedEvents.OfType<ActionCapabilityFilteredEvent>().Count().ShouldBe(1);

        var evt = _emittedEvents.OfType<ActionCapabilityFilteredEvent>().Single();
        evt.Context.ActionName.ShouldBe("Helm Upgrade");
        evt.Context.MachineName.ShouldBe("ssh-target");
        evt.Context.Message.ShouldContain("Squid.HelmChartUpgrade");
    }

    [Fact]
    public async Task Action_ExecutedWhenDispatchValidationPasses()
    {
        var handler = InstallHandler();
        var action = BuildAction(1, "Run Script", SpecialVariables.ActionTypes.Script);
        var target = BuildTarget(1, "ssh-target");

        _ctx.Steps = new List<DeploymentStepDto> { BuildStep(10, "Deploy", action) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };

        SeedPlan(BuildPlannedStep(10,
            BuildPlannedAction(1, SpecialVariables.ActionTypes.Script,
                BuildDispatch(1, "ssh-target", isValid: true))));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        handler.DescribeIntentCallCount.ShouldBe(1);
        _sshRenderer.CapturedIntents.Count.ShouldBe(1);
        _emittedEvents.OfType<ActionCapabilityFilteredEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task MultipleActions_OnlyUnsupportedSkipped()
    {
        var scriptHandler = new TrackingHandler { ActionType = SpecialVariables.ActionTypes.Script };
        var helmHandler = new TrackingHandler { ActionType = "Squid.HelmChartUpgrade" };

        _handlerRegistryMock.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.TargetLevel);
        _handlerRegistryMock.Setup(r => r.Resolve(It.Is<DeploymentActionDto>(a => a.ActionType == SpecialVariables.ActionTypes.Script))).Returns(scriptHandler);
        _handlerRegistryMock.Setup(r => r.Resolve(It.Is<DeploymentActionDto>(a => a.ActionType == "Squid.HelmChartUpgrade"))).Returns(helmHandler);

        var scriptAction = BuildAction(1, "Run Script", SpecialVariables.ActionTypes.Script);
        var helmAction = BuildAction(2, "Helm Upgrade", "Squid.HelmChartUpgrade");
        var target = BuildTarget(1, "ssh-target");

        _ctx.Steps = new List<DeploymentStepDto> { BuildStep(10, "Mixed Step", scriptAction, helmAction) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };

        SeedPlan(BuildPlannedStep(10,
            BuildPlannedAction(1, SpecialVariables.ActionTypes.Script,
                BuildDispatch(1, "ssh-target", isValid: true)),
            BuildPlannedAction(2, "Squid.HelmChartUpgrade",
                BuildDispatch(1, "ssh-target", isValid: false, "unsupported action type"))));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        scriptHandler.DescribeIntentCallCount.ShouldBe(1);
        helmHandler.DescribeIntentCallCount.ShouldBe(0);
        _emittedEvents.OfType<ActionCapabilityFilteredEvent>().Count().ShouldBe(1);
        _emittedEvents.OfType<ActionCapabilityFilteredEvent>().Single().Context.ActionName.ShouldBe("Helm Upgrade");
    }

    [Fact]
    public async Task MultipleTargets_OnlyUnsupportedTargetSkipped()
    {
        var handler = InstallHandler();
        var helmAction = BuildAction(1, "Helm Upgrade", "Squid.HelmChartUpgrade");

        var k8sTarget = BuildTarget(1, "k8s-target", CommunicationStyle.KubernetesApi);
        var sshTarget = BuildTarget(2, "ssh-target", CommunicationStyle.Ssh);

        _ctx.Steps = new List<DeploymentStepDto> { BuildStep(10, "Deploy Helm", helmAction) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { k8sTarget, sshTarget };

        SeedPlan(BuildPlannedStep(10,
            BuildPlannedAction(1, "Squid.HelmChartUpgrade",
                BuildDispatch(1, "k8s-target", isValid: true),
                BuildDispatch(2, "ssh-target", isValid: false, "unsupported"))));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // Handler called once (for k8s target only)
        handler.DescribeIntentCallCount.ShouldBe(1);
        _k8sRenderer.CapturedIntents.Count.ShouldBe(1);

        // One capability filtered event for SSH target
        var filtered = _emittedEvents.OfType<ActionCapabilityFilteredEvent>().ToList();
        filtered.Count.ShouldBe(1);
        filtered[0].Context.MachineName.ShouldBe("ssh-target");
    }

    [Fact]
    public async Task NoPlanPresent_FallbackPathUnchanged()
    {
        var handler = InstallHandler();
        var action = BuildAction(1, "Run Script", SpecialVariables.ActionTypes.Script);
        var target = BuildTarget(1, "target-1");

        _ctx.Steps = new List<DeploymentStepDto> { BuildStep(10, "Deploy", action) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };
        _ctx.Plan = null;

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        handler.DescribeIntentCallCount.ShouldBe(1);
        _sshRenderer.CapturedIntents.Count.ShouldBe(1);
        _emittedEvents.OfType<ActionCapabilityFilteredEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task AllDispatchesInvalid_StepEmitsStartAndComplete_NotFailed()
    {
        InstallHandler();
        var action = BuildAction(1, "Helm Upgrade", "Squid.HelmChartUpgrade");
        var target = BuildTarget(1, "ssh-target");

        _ctx.Steps = new List<DeploymentStepDto> { BuildStep(10, "Deploy", action) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };

        SeedPlan(BuildPlannedStep(10,
            BuildPlannedAction(1, "Squid.HelmChartUpgrade",
                BuildDispatch(1, "ssh-target", isValid: false))));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // Step lifecycle: started and completed
        _emittedEvents.OfType<StepStartingEvent>().Count().ShouldBe(1);
        _emittedEvents.OfType<StepCompletedEvent>().Count().ShouldBe(1);

        // Step did not fail
        var completed = _emittedEvents.OfType<StepCompletedEvent>().Single();
        completed.Context.Failed.ShouldBeFalse();

        // FailureEncountered should NOT have been set
        _ctx.FailureEncountered.ShouldBeFalse();
    }

    [Fact]
    public async Task RunOnServerStep_NoCapabilityFiltering()
    {
        var handler = InstallHandler();
        var action = BuildAction(1, "Run Script", SpecialVariables.ActionTypes.Script);

        var step = BuildStep(10, "Server Step", action);
        step.Properties.Add(new DeploymentStepPropertyDto
        {
            PropertyName = SpecialVariables.Step.RunOnServer,
            PropertyValue = "true"
        });

        _ctx.Steps = new List<DeploymentStepDto> { step };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext>();

        // Plan marks it as RunOnServer with no dispatches
        SeedPlan(new PlannedStep
        {
            StepId = 10,
            StepName = "Server Step",
            StepOrder = 1,
            Status = PlannedStepStatus.RunOnServer,
            Actions = new[]
            {
                new PlannedAction
                {
                    ActionId = 1,
                    ActionName = "Run Script",
                    ActionType = SpecialVariables.ActionTypes.Script,
                    Dispatches = Array.Empty<PlannedTargetDispatch>()
                }
            }
        });

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // Handler should have been called (RunOnServer uses PrepareStepActionsAsync too)
        handler.DescribeIntentCallCount.ShouldBe(1);
        _emittedEvents.OfType<ActionCapabilityFilteredEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchMissingForAction_FallbackToNoFilter()
    {
        var handler = InstallHandler();
        var action = BuildAction(1, "Run Script", SpecialVariables.ActionTypes.Script);
        var target = BuildTarget(1, "target-1");

        _ctx.Steps = new List<DeploymentStepDto> { BuildStep(10, "Deploy", action) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };

        // Plan has the action but dispatch targets a *different* machine — (action=1, machine=1) has no dispatch.
        // Manually include machine 1 in MatchedTargets (as if another action routed it there).
        SeedPlan(new PlannedStep
        {
            StepId = 10,
            StepName = "Deploy",
            StepOrder = 1,
            Status = PlannedStepStatus.Applicable,
            MatchedTargets = new[] { new PlannedTarget { MachineId = 1, MachineName = "target-1" } },
            Actions = new[]
            {
                new PlannedAction
                {
                    ActionId = 1,
                    ActionName = "Run Script",
                    ActionType = SpecialVariables.ActionTypes.Script,
                    Dispatches = new[]
                    {
                        BuildDispatch(99, "other-target", isValid: false) // dispatch for machine 99, not machine 1
                    }
                }
            }
        });

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // Action proceeds because dispatch lookup returns null for machine 1 → no filtering
        handler.DescribeIntentCallCount.ShouldBe(1);
        _emittedEvents.OfType<ActionCapabilityFilteredEvent>().ShouldBeEmpty();
    }
}
