using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using Squid.Core.Services.DeploymentExecution.Transport;

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

    // ========== Guided Failure: Retry ==========

    [Fact]
    public async Task GuidedFailure_Retry_ReExecutesAction()
    {
        var callCount = 0;

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });
        interruptionService.Setup(s => s.WaitForInterruptionAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterruptionOutcome.Retry);

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);

        var retryStrategy = new TestExecutionStrategy(_ =>
        {
            callCount++;
            if (callCount == 1)
                return new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "fail" } };
            return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } };
        });
        var ctx = CreateBaseContext(useGuidedFailure: true, strategy: retryStrategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        callCount.ShouldBe(2);
        ctx.FailureEncountered.ShouldBeFalse();
    }

    // ========== Guided Failure: Skip ==========

    [Fact]
    public async Task GuidedFailure_Skip_ContinuesWithoutFailure()
    {
        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "fail" } });

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });
        interruptionService.Setup(s => s.WaitForInterruptionAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterruptionOutcome.Skip);

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.FailureEncountered.ShouldBeFalse();
    }

    // ========== Guided Failure: Abort ==========

    [Fact]
    public async Task GuidedFailure_Abort_Throws()
    {
        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "fail" } });

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });
        interruptionService.Setup(s => s.WaitForInterruptionAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterruptionOutcome.Abort);

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: true);
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<Exception>(() => phase.ExecuteAsync(ctx, CancellationToken.None));
    }

    // ========== Guided Failure: Disabled → Throws Directly ==========

    [Fact]
    public async Task GuidedFailure_Disabled_ThrowsDirectly()
    {
        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "fail" } });

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext(useGuidedFailure: false);
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<Exception>(() => phase.ExecuteAsync(ctx, CancellationToken.None));

        interruptionService.Verify(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========== Guided Failure: Checkpoint Before Waiting ==========

    [Fact]
    public async Task GuidedFailure_Retry_PersistsCheckpointBeforeWaiting()
    {
        var callOrder = new List<string>();

        var checkpointService = new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>();
        checkpointService.Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("checkpoint"))
            .Returns(Task.CompletedTask);

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });
        interruptionService.Setup(s => s.WaitForInterruptionAsync(1, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("wait"))
            .ReturnsAsync(InterruptionOutcome.Retry);

        var callCount = 0;
        var retryStrategy = new TestExecutionStrategy(_ =>
        {
            callCount++;
            if (callCount == 1)
                return new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "fail" } };
            return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } };
        });

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, checkpointService.Object);
        var ctx = CreateBaseContext(useGuidedFailure: true, strategy: retryStrategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        callOrder.ShouldContain("checkpoint");
        callOrder.ShouldContain("wait");
        callOrder.IndexOf("checkpoint").ShouldBeLessThan(callOrder.IndexOf("wait"));
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

    private static DeploymentTaskContext CreateBaseContext(bool useGuidedFailure = false, IExecutionStrategy strategy = null)
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
                            ActionType = "Octopus.Script", IsDisabled = false,
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
