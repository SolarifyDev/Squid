using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Interruptions;

public class GuidedFailureTests
{
    // ========== TaskState Transitions ==========

    [Fact]
    public void TaskState_ExecutingToPaused_IsValid()
        => TaskState.IsValidTransition(TaskState.Executing, TaskState.Paused).ShouldBeTrue();

    [Fact]
    public void TaskState_PausedToExecuting_IsValid()
        => TaskState.IsValidTransition(TaskState.Paused, TaskState.Executing).ShouldBeTrue();

    [Fact]
    public void TaskState_PausedToFailed_IsValid()
        => TaskState.IsValidTransition(TaskState.Paused, TaskState.Failed).ShouldBeTrue();

    [Fact]
    public void TaskState_PausedToCancelled_IsValid()
        => TaskState.IsValidTransition(TaskState.Paused, TaskState.Cancelled).ShouldBeTrue();

    [Fact]
    public void TaskState_PausedToSuccess_IsInvalid()
        => TaskState.IsValidTransition(TaskState.Paused, TaskState.Success).ShouldBeFalse();

    [Fact]
    public void TaskState_PendingToPaused_IsInvalid()
        => TaskState.IsValidTransition(TaskState.Pending, TaskState.Paused).ShouldBeFalse();

    [Fact]
    public void TaskState_Paused_IsActive()
        => TaskState.IsActive(TaskState.Paused).ShouldBeTrue();

    [Fact]
    public void TaskState_Paused_IsNotTerminal()
        => TaskState.IsTerminal(TaskState.Paused).ShouldBeFalse();

    [Fact]
    public void TaskState_Paused_IsValid()
        => TaskState.IsValid(TaskState.Paused).ShouldBeTrue();

    // ========== Guided Failure: Suspend ==========

    [Fact]
    public async Task GuidedFailure_ActionFails_ThrowsSuspendedException()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true);
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<DeploymentSuspendedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None));

        interruptionService.Verify(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), TaskState.Executing, TaskState.Paused, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Guided Failure: Checkpoint Before Suspending ==========

    [Fact]
    public async Task GuidedFailure_ActionFails_PersistsCheckpointBeforeSuspending()
    {
        var callOrder = new List<string>();

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService.Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("checkpoint"))
            .Returns(Task.CompletedTask);

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });

        var serverTaskService = new Mock<IServerTaskService>();
        serverTaskService.Setup(s => s.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("transition"))
            .Returns(Task.CompletedTask);

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, checkpointService.Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true);
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<DeploymentSuspendedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None));

        callOrder.ShouldContain("checkpoint");
        callOrder.ShouldContain("transition");
        callOrder.IndexOf("checkpoint").ShouldBeLessThan(callOrder.IndexOf("transition"));
    }

    // ========== Guided Failure: Resume with Retry ==========

    [Fact]
    public async Task GuidedFailure_ResumeWithRetry_ReExecutesAction()
    {
        var callCount = 0;
        var retryStrategy = new TestExecutionStrategy(_ =>
        {
            callCount++;
            return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } };
        });

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Retry" });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true, isResume: true, strategy: retryStrategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        callCount.ShouldBe(1);
        ctx.FailureEncountered.ShouldBeFalse();
    }

    // ========== Guided Failure: Resume with Skip ==========

    [Fact]
    public async Task GuidedFailure_ResumeWithSkip_SkipsAction()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Skip" });

        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true, isResume: true, strategy: strategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.FailureEncountered.ShouldBeFalse();
    }

    // ========== Guided Failure: Resume with Abort ==========

    [Fact]
    public async Task GuidedFailure_ResumeWithAbort_ThrowsAbortedException()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Abort" });

        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true, isResume: true, strategy: strategy);
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<DeploymentAbortedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None));
    }

    // ========== Guided Failure: Disabled → Throws Directly ==========

    [Fact]
    public async Task GuidedFailure_Disabled_ThrowsDirectly()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: false);
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<Exception>(() => phase.ExecuteAsync(ctx, CancellationToken.None));

        interruptionService.Verify(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========== Guided Failure: Retry then fail again → suspends again ==========

    [Fact]
    public async Task GuidedFailure_RetryThenFailsAgain_SuspendsAgain()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Retry" });
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 2 });

        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "fail again" } });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true, isResume: true, strategy: strategy);
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<DeploymentSuspendedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None));

        interruptionService.Verify(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        serverTaskService.Verify(s => s.TransitionStateAsync(It.IsAny<int>(), TaskState.Executing, TaskState.Paused, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Helpers ==========

    private static (IDeploymentLifecycle, List<DeploymentLifecycleEvent>) CreateLifecycle()
    {
        var events = new List<DeploymentLifecycleEvent>();
        var lifecycle = new DeploymentLifecyclePublisher(new List<IDeploymentLifecycleHandler>());

        return (lifecycle, events);
    }

    private static IActionHandlerRegistry CreateRegistry()
    {
        var handler = new Mock<IActionHandler>();
        handler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        handler.Setup(h => h.PrepareAsync(It.IsAny<ActionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionExecutionResult
            {
                ScriptBody = "echo test",
                Syntax = ScriptSyntax.Bash,
                Files = new Dictionary<string, byte[]>(),
                ExecutionMode = ExecutionMode.DirectScript,
                ContextPreparationPolicy = ContextPreparationPolicy.Apply
            });

        return Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler.Object);
    }

    private static DeploymentTaskContext CreateBaseContext(bool useGuidedFailure = false, bool isResume = false, IExecutionStrategy strategy = null)
    {
        var effectiveStrategy = strategy ?? new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "script failed" } });
        var transport = new TestTransport(effectiveStrategy, scriptWrapper: null);

        return new DeploymentTaskContext
        {
            Deployment = new Deployment { Id = 1, SpaceId = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Project = new Project { Id = 1, Name = "Test" },
            UseGuidedFailure = useGuidedFailure,
            IsResume = isResume,
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>
            {
                new()
                {
                    Id = 1, StepOrder = 1, Name = "Step 1", StepType = "Action",
                    Condition = "Success", IsDisabled = false, IsRequired = true, StartTrigger = "StartAfterPrevious",
                    Properties = new List<DeploymentStepPropertyDto>(),
                    Actions = new List<DeploymentActionDto>
                    {
                        new()
                        {
                            Id = 1, StepId = 1, ActionOrder = 1, Name = "Action 1",
                            ActionType = SpecialVariables.ActionTypes.Script, IsDisabled = false,
                            Properties = new List<DeploymentActionPropertyDto>(),
                            Environments = new List<int>(),
                            ExcludedEnvironments = new List<int>(),
                            Channels = new List<int>()
                        }
                    }
                }
            },
            AllTargets = new List<Machine> { new() { Id = 1, Name = "machine-1", HealthStatus = MachineHealthStatus.Healthy } },
            AllTargetsContext = new List<DeploymentTargetContext>
            {
                new()
                {
                    Machine = new Machine { Id = 1, Name = "machine-1", Roles = "[\"web\"]", HealthStatus = MachineHealthStatus.Healthy },
                    Transport = transport,
                    CommunicationStyle = CommunicationStyle.KubernetesApi
                }
            }
        };
    }

    private class TestExecutionStrategy : IExecutionStrategy
    {
        private readonly Func<ScriptExecutionRequest, ScriptExecutionResult> _handler;

        public TestExecutionStrategy(Func<ScriptExecutionRequest, ScriptExecutionResult> handler) => _handler = handler;

        public bool CanHandle(string communicationStyle) => true;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }

    private class TestTransport : IDeploymentTransport
    {
        public TestTransport(IExecutionStrategy strategy, IScriptContextWrapper scriptWrapper)
        {
            Strategy = strategy;
            ScriptWrapper = scriptWrapper;
        }

        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesApi;
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper { get; }
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.Unspecified;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.Unspecified;
        public bool RequiresContextPreparationForPackagedPayload => false;
    }
}
