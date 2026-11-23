using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.UnitTests.Services.Deployments.Execution.Handlers;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.Interruptions;

/// <summary>
/// A guided failure in one step of a parallel batch moves the task to Paused. The sibling step must STOP (its script is left running on the agent) instead
/// of carrying on behind a Paused task that a resume would then run beside; and the suspension, not the siblings' cancellation, is what surfaces.
/// Deleting the suspend token from the phase's batch execution leaves the sibling running forever and times these out.
/// </summary>
public class SuspendStopsParallelSiblingsTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<IServerTaskLease> _lease = new();
    private readonly BlockingStrategy _siblingStrategy = new();

    [Fact]
    public async Task GuidedFailureInOneStep_StopsTheSiblingStep_AndTheSuspensionSurfaces()
    {
        var (phase, ctx) = Build();

        var ex = await Should.ThrowAsync<DeploymentSuspendedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None)).WaitAsync(Bound);

        _siblingStrategy.Stopped.ShouldBeTrue("the sibling's token must be cancelled by the suspend");
        ctx.Suspended.ShouldBeSameAs(ex);
        ctx.SuspendToken.IsCancellationRequested.ShouldBeTrue();
    }

    // A sibling being stopped may fail in ways that are not a cancellation (its agent turned unreachable mid-call, a transient failure that propagates); Task.WhenAll would surface that exception, not the
    // suspension. The task is already Paused, so the suspension is what the runner must see.
    [Fact]
    public async Task ASiblingThatThrowsSomethingElseWhileBeingStopped_DoesNotMaskTheSuspension()
    {
        _siblingStrategy.ThrowOnStop = new Squid.Core.Halibut.Resilience.AgentUnreachableException("web-1", 3);
        var (phase, ctx) = Build();

        await Should.ThrowAsync<DeploymentSuspendedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None)).WaitAsync(Bound);
    }

    // The pause is written under this run's lease, after the run released its own lease (so the keeper does not read the pause as a loss) and before the
    // siblings are stopped (they leave their scripts running because the task is already Paused).
    [Fact]
    public async Task TheSuspend_ReleasesTheLease_ThenPausesUnderTheRunsOwner_ThenStopsTheSiblings()
    {
        var order = new List<string>();
        _lease.Setup(l => l.Release()).Callback(() => order.Add("release"));
        _serverTaskService.Setup(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Paused, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("pause")).Returns(Task.CompletedTask);
        _siblingStrategy.OnStopped = () => order.Add("sibling-stopped");
        var (phase, ctx) = Build();

        await Should.ThrowAsync<DeploymentSuspendedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None)).WaitAsync(Bound);

        order.ShouldBe(["release", "pause", "sibling-stopped"]);
        _serverTaskService.Verify(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Paused, ctx.LeaseOwner, It.IsAny<CancellationToken>()), Times.Once);
    }

    // If the pause itself fails (a user cancel landed first), nothing was suspended: the siblings must not be told to leave their scripts running.
    [Fact]
    public async Task WhenThePauseWriteFails_NothingIsSuspended()
    {
        using var cts = new CancellationTokenSource();
        _serverTaskService.Setup(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Paused, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.CancelAfter(100))
            .ThrowsAsync(new InvalidOperationException("Cancelling"));
        var (phase, ctx) = Build();

        await Should.ThrowAsync<Exception>(() => phase.ExecuteAsync(ctx, cts.Token)).WaitAsync(Bound);

        ctx.Suspended.ShouldBeNull();
        ctx.SuspendToken.IsCancellationRequested.ShouldBeFalse();
    }

    private (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx) Build()
    {
        var interruptions = new Mock<IDeploymentInterruptionService>();
        interruptions.Setup(i => i.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DeploymentInterruption { Id = 1 });

        var handler = new Mock<IActionHandler>();
        handler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        handler.SetupDescribeIntentAsRunScript();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler.Object);

        var lifecycle = new DeploymentLifecyclePublisher(new List<IDeploymentLifecycleHandler>());
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptions.Object, new Mock<IDeploymentCheckpointService>().Object, _serverTaskService.Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create(), new Mock<Squid.Core.Services.Security.IVariableEncryptionService>().Object);

        var ctx = Context();
        lifecycle.Initialize(ctx);

        return (phase, ctx);
    }

    // Step 1 (role "web") runs on a machine whose script blocks until its token is cancelled; step 2 (StartWithPrevious, role "db") fails on its own machine
    // into a guided failure. Step 1 comes first, so when it throws its cancellation first it is the exception Task.WhenAll would surface.
    private DeploymentTaskContext Context() => new()
    {
        ServerTaskId = 1,
        Lease = _lease.Object,
        UseGuidedFailure = true,
        Deployment = new Deployment { Id = 1, SpaceId = 1, EnvironmentId = 1, ChannelId = 1 },
        Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
        Project = new Project { Id = 1, Name = "Test" },
        Variables = new List<VariableDto>(),
        SelectedPackages = new List<ReleaseSelectedPackage>(),
        Steps = [Step(1, "Blocker", "web", "StartAfterPrevious"), Step(2, "Failer", "db", "StartWithPrevious")],
        AllTargets = [new Machine { Id = 1, Name = "web-1" }, new Machine { Id = 2, Name = "db-1" }],
        AllTargetsContext = [Target(1, "web-1", "web", _siblingStrategy), Target(2, "db-1", "db", new FailingStrategy())]
    };

    private static DeploymentStepDto Step(int id, string name, string role, string trigger) => new()
    {
        Id = id, StepOrder = id, Name = name, StepType = "Action", Condition = "Success", IsDisabled = false, IsRequired = true, StartTrigger = trigger,
        Properties = [new DeploymentStepPropertyDto { PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = role }],
        Actions =
        [
            new DeploymentActionDto
            {
                Id = id, StepId = id, ActionOrder = 1, Name = name, ActionType = SpecialVariables.ActionTypes.Script, IsDisabled = false,
                Properties = [], Environments = [], ExcludedEnvironments = [], Channels = []
            }
        ]
    };

    private static DeploymentTargetContext Target(int id, string name, string role, IExecutionStrategy strategy) => new()
    {
        Machine = new Machine { Id = id, Name = name, Roles = $"[\"{role}\"]", HealthStatus = MachineHealthStatus.Healthy },
        Transport = new Transport(strategy),
        CommunicationStyle = CommunicationStyle.KubernetesApi
    };

    private sealed class BlockingStrategy : IExecutionStrategy
    {
        private volatile bool _stopped;

        public bool Stopped => _stopped;

        public Action OnStopped { get; set; }

        public Exception ThrowOnStop { get; set; }

        public bool CanHandle(string communicationStyle) => true;

        public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                _stopped = true;
                OnStopped?.Invoke();

                if (ThrowOnStop != null) throw ThrowOnStop;

                throw;
            }

            return new ScriptExecutionResult { Success = true };
        }
    }

    private sealed class FailingStrategy : IExecutionStrategy
    {
        public bool CanHandle(string communicationStyle) => true;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
            => Task.FromResult(new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = ["script failed"] });
    }

    private sealed class Transport(IExecutionStrategy strategy) : IDeploymentTransport
    {
        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesApi;
        public IEndpointVariableContributor Variables => null;
        public IExecutionStrategy Strategy { get; } = strategy;
        public IHealthCheckStrategy HealthChecker => null;
        public ITransportCapabilities Capabilities { get; } = new TransportCapabilities();
    }
}
