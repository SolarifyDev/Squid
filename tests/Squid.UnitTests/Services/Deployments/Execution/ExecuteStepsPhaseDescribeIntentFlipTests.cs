using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Core.Services.DeploymentExecution.Transport;
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
/// Phase 9h — proves the pipeline renders intents produced by
/// <see cref="IActionHandler.DescribeIntentAsync"/> directly, bypassing the legacy
/// <c>PrepareAsync</c> path entirely. The test installs a handler whose explicit
/// <c>DescribeIntentAsync</c> override returns a distinctive <see cref="RunScriptIntent"/>
/// (different <c>Name</c> and <c>ScriptBody</c> than its legacy <c>PrepareAsync</c>
/// output) and captures the intent that reaches the renderer via a probe
/// <see cref="IIntentRenderer"/>. The captured intent must be the one returned by
/// <c>DescribeIntentAsync</c>, not any value derived from <c>PrepareAsync</c>.
/// </summary>
public class ExecuteStepsPhaseDescribeIntentFlipTests : IDisposable
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
    private readonly CapturingProbeRenderer _capturingRenderer;

    public ExecuteStepsPhaseDescribeIntentFlipTests()
    {
        _capturingRenderer = new CapturingProbeRenderer(CommunicationStyle.Ssh);

        _lifecycleMock = new Mock<IDeploymentLifecycle>();
        _lifecycleMock.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

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

        var rendererRegistry = new IntentRendererRegistry(new IIntentRenderer[] { _capturingRenderer });

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

    private const string DescribeIntentScriptBody = "echo from-describe-intent-override";
    private const string DescribeIntentName = "probe-describe-intent-name";

    /// <summary>
    /// Fake handler whose <c>DescribeIntentAsync</c> returns a distinctive
    /// <see cref="RunScriptIntent"/> so tests can verify the intent that reaches the renderer.
    /// </summary>
    private sealed class ProbeHandler : IActionHandler
    {
        public string ActionType => SpecialVariables.ActionTypes.Script;

        public int DescribeIntentCallCount { get; private set; }

        public Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            DescribeIntentCallCount++;
            return Task.FromResult<ExecutionIntent>(new RunScriptIntent
            {
                Name = DescribeIntentName,
                StepName = ctx.Step?.Name ?? string.Empty,
                ActionName = ctx.Action?.Name ?? string.Empty,
                ScriptBody = DescribeIntentScriptBody,
                Syntax = ScriptSyntax.Bash,
                InjectRuntimeBundle = false
            });
        }
    }

    /// <summary>
    /// Probe renderer that captures every intent it receives. Returns a basic
    /// <see cref="ScriptExecutionRequest"/> so the execution strategy sees a valid shape.
    /// </summary>
    private sealed class CapturingProbeRenderer : IIntentRenderer
    {
        public CapturingProbeRenderer(CommunicationStyle style)
        {
            CommunicationStyle = style;
        }

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
    // Scenario wiring
    // ------------------------------------------------------------------

    private ProbeHandler InstallProbeHandler()
    {
        var handler = new ProbeHandler();
        _handlerRegistryMock.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.TargetLevel);
        _handlerRegistryMock.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(handler);
        return handler;
    }

    private void SeedSingleScriptStep(string stepName = "Deploy", string actionName = "Run Deploy")
    {
        _ctx.Steps = new List<DeploymentStepDto>
        {
            new()
            {
                Id = 1,
                StepOrder = 1,
                Name = stepName,
                StepType = "Action",
                Condition = "Success",
                StartTrigger = "StartAfterPrevious",
                IsRequired = true,
                Properties = new List<DeploymentStepPropertyDto>(),
                Actions = new List<DeploymentActionDto>
                {
                    new()
                    {
                        Id = 1,
                        ActionOrder = 1,
                        Name = actionName,
                        ActionType = SpecialVariables.ActionTypes.Script,
                        IsDisabled = false,
                        Properties = new List<DeploymentActionPropertyDto>()
                    }
                }
            }
        };

        _ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            new()
            {
                Machine = new Machine { Id = 1, Name = "target-1", Roles = string.Empty },
                CommunicationStyle = CommunicationStyle.Ssh,
                Transport = _transportMock.Object,
                EndpointContext = new EndpointContext()
            }
        };
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task RenderIntent_UsesHandlerDescribeIntent_IntentNameMatchesOverride()
    {
        InstallProbeHandler();
        SeedSingleScriptStep();

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturingRenderer.CapturedIntents.Count.ShouldBe(1);
        _capturingRenderer.CapturedIntents[0].Name.ShouldBe(DescribeIntentName);
    }

    [Fact]
    public async Task RenderIntent_UsesHandlerDescribeIntent_NameIsNotLegacyPrefixed()
    {
        InstallProbeHandler();
        SeedSingleScriptStep();

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturingRenderer.CapturedIntents.Count.ShouldBe(1);
        _capturingRenderer.CapturedIntents[0].Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task RenderIntent_UsesHandlerDescribeIntent_ScriptBodyFromOverride()
    {
        InstallProbeHandler();
        SeedSingleScriptStep();

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturingRenderer.CapturedIntents.Count.ShouldBe(1);
        var intent = _capturingRenderer.CapturedIntents[0].ShouldBeOfType<RunScriptIntent>();
        intent.ScriptBody.ShouldBe(DescribeIntentScriptBody);
    }

    [Fact]
    public async Task RenderIntent_CallsDescribeIntentAtLeastOnce()
    {
        var handler = InstallProbeHandler();
        SeedSingleScriptStep();

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        handler.DescribeIntentCallCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task RenderIntent_PropagatesStepAndActionName()
    {
        InstallProbeHandler();
        SeedSingleScriptStep(stepName: "Deploy App", actionName: "Deploy Web Action");

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturingRenderer.CapturedIntents.Count.ShouldBe(1);
        var intent = _capturingRenderer.CapturedIntents[0];
        intent.StepName.ShouldBe("Deploy App");
        intent.ActionName.ShouldBe("Deploy Web Action");
    }
}
