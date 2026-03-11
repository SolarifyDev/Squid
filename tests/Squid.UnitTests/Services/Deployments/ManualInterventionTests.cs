using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

public class ManualInterventionTests
{
    [Fact]
    public async Task ManualIntervention_Proceed_ContinuesExecution()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });
        interruptionService.Setup(s => s.WaitForInterruptionAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterruptionOutcome.Proceed);

        var serverTaskService = new Mock<IServerTaskService>();
        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateManualInterventionRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, serverTaskService.Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.FailureEncountered.ShouldBeFalse();
        interruptionService.Verify(s => s.CreateInterruptionAsync(It.Is<CreateInterruptionRequest>(r => r.InterruptionType == InterruptionType.ManualIntervention), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManualIntervention_Abort_ThrowsDeploymentAbortedException()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });
        interruptionService.Setup(s => s.WaitForInterruptionAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterruptionOutcome.Abort);

        var serverTaskService = new Mock<IServerTaskService>();
        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateManualInterventionRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, serverTaskService.Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await Should.ThrowAsync<DeploymentAbortedException>(() => phase.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task ManualIntervention_CreatesInterruptionWithForm()
    {
        CreateInterruptionRequest capturedRequest = null;

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateInterruptionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });
        interruptionService.Setup(s => s.WaitForInterruptionAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InterruptionOutcome.Proceed);

        var serverTaskService = new Mock<IServerTaskService>();
        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateManualInterventionRegistry("Please approve this");
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, serverTaskService.Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.InterruptionType.ShouldBe(InterruptionType.ManualIntervention);
        capturedRequest.Form.ShouldNotBeNull();
        capturedRequest.Form.Elements.Count.ShouldBe(3);
    }

    // ========== Helpers ==========

    private static (IDeploymentLifecycle, List<DeploymentLifecycleEvent>) CreateLifecycle()
    {
        var events = new List<DeploymentLifecycleEvent>();
        var lifecycle = new DeploymentLifecyclePublisher(new List<IDeploymentLifecycleHandler>());
        return (lifecycle, events);
    }

    private static IActionHandlerRegistry CreateManualInterventionRegistry(string instructions = "Please verify")
    {
        var handler = new Mock<IActionHandler>();
        handler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        handler.Setup(h => h.PrepareAsync(It.IsAny<ActionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionExecutionResult
            {
                ActionName = "Manual Step",
                ExecutionMode = ExecutionMode.ManualIntervention,
                ContextPreparationPolicy = ContextPreparationPolicy.Skip,
                ManualInterventionInstructions = instructions
            });

        return Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler.Object);
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
        var transport = new TestTransport(strategy, scriptWrapper: null);

        return new DeploymentTaskContext
        {
            Deployment = new Deployment { Id = 1, SpaceId = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new Release { Id = 1, Version = "1.0.0" },
            Project = new Project { Id = 1, Name = "Test" },
            UseGuidedFailure = false,
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
                            Id = 1, StepId = 1, ActionOrder = 1, Name = "Manual Action",
                            ActionType = "Squid.ManualIntervention", IsDisabled = false,
                            Properties = new List<DeploymentActionPropertyDto>
                            {
                                new() { PropertyName = "Squid.Action.Manual.Instructions", PropertyValue = "Please verify" }
                            },
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
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct) => Task.FromResult(_handler(request));
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
