using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Handlers;
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

namespace Squid.UnitTests.Services.Deployments.Interruptions;

public class ManualInterventionTests
{
    // ========== Handler Tests ==========

    [Fact]
    public void Handler_CanHandle_SquidManualActionType()
    {
        var handler = CreateHandler();

        var action = new DeploymentActionDto { ActionType = "Squid.Manual" };

        handler.CanHandle(action).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Squid.KubernetesRunScript")]
    [InlineData("Squid.KubernetesDeployRawYaml")]
    [InlineData(null)]
    [InlineData("")]
    public void Handler_CanHandle_RejectNonManualTypes(string actionType)
    {
        var handler = CreateHandler();

        var action = new DeploymentActionDto { ActionType = actionType };

        handler.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void Handler_ExecutionScope_IsStepLevel()
    {
        var handler = CreateHandler();

        handler.ExecutionScope.ShouldBe(ExecutionScope.StepLevel);
    }

    [Theory]
    [InlineData("Please approve", "Please approve")]
    [InlineData(null, "")]
    public async Task Handler_PrepareAsync_ReturnsManualInterventionMode(string instructions, string expectedInstructions)
    {
        var handler = CreateHandler();
        var properties = new List<DeploymentActionPropertyDto>();

        if (instructions != null)
            properties.Add(new DeploymentActionPropertyDto { PropertyName = "Squid.Action.Manual.Instructions", PropertyValue = instructions });

        var ctx = new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Id = 1, Name = "Step" },
            Action = new DeploymentActionDto { Id = 1, Name = "Manual Action", ActionType = "Squid.Manual", Properties = properties }
        };

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.ManualIntervention);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
        result.ManualInterventionInstructions.ShouldBe(expectedInstructions);
        result.ScriptBody.ShouldBeNull();
    }

    // ========== ExecuteStepLevelAsync Tests ==========

    [Fact]
    public async Task ExecuteStepLevelAsync_FirstRun_CreatesInterruptionAndThrowsSuspended()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeploymentInterruption)null);
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });

        var serverTaskService = new Mock<IServerTaskService>();
        var lifecycle = new Mock<IDeploymentLifecycle>();

        var handler = new ManualInterventionActionHandler(interruptionService.Object, serverTaskService.Object, lifecycle.Object);
        var ctx = CreateStepActionContext();

        await Should.ThrowAsync<DeploymentSuspendedException>(() => handler.ExecuteStepLevelAsync(ctx, CancellationToken.None));

        interruptionService.Verify(s => s.CreateInterruptionAsync(It.Is<CreateInterruptionRequest>(r => r.InterruptionType == InterruptionType.ManualIntervention), It.IsAny<CancellationToken>()), Times.Once);
        serverTaskService.Verify(s => s.TransitionStateAsync(ctx.ServerTaskId, TaskState.Executing, TaskState.Paused, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_ResumeWithProceed_ReturnsNormally()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Proceed" });

        var handler = new ManualInterventionActionHandler(interruptionService.Object, new Mock<IServerTaskService>().Object, new Mock<IDeploymentLifecycle>().Object);
        var ctx = CreateStepActionContext();

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        interruptionService.Verify(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_ResumeWithAbort_ThrowsDeploymentAbortedException()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Abort" });

        var handler = new ManualInterventionActionHandler(interruptionService.Object, new Mock<IServerTaskService>().Object, new Mock<IDeploymentLifecycle>().Object);
        var ctx = CreateStepActionContext();

        await Should.ThrowAsync<DeploymentAbortedException>(() => handler.ExecuteStepLevelAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_FirstRun_EmitsManualInterventionPromptEvent()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeploymentInterruption)null);
        interruptionService.Setup(s => s.CreateInterruptionAsync(It.IsAny<CreateInterruptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Id = 1 });

        var serverTaskService = new Mock<IServerTaskService>();
        var lifecycle = new Mock<IDeploymentLifecycle>();

        var handler = new ManualInterventionActionHandler(interruptionService.Object, serverTaskService.Object, lifecycle.Object);
        var ctx = CreateStepActionContext();

        await Should.ThrowAsync<DeploymentSuspendedException>(() => handler.ExecuteStepLevelAsync(ctx, CancellationToken.None));

        lifecycle.Verify(l => l.EmitAsync(It.IsAny<ManualInterventionPromptEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Step-Level Only: No Per-Target Execution ==========

    [Fact]
    public async Task Pipeline_ManualInterventionStep_DoesNotEmitPerTargetEvents()
    {
        var emittedEvents = new List<DeploymentLifecycleEvent>();

        var lifecycle = new Mock<IDeploymentLifecycle>();
        lifecycle.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentLifecycleEvent, CancellationToken>((e, _) => emittedEvents.Add(e))
            .Returns(Task.CompletedTask);

        var manualHandler = new Mock<IActionHandler>();
        manualHandler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        manualHandler.Setup(h => h.ExecutionScope).Returns(ExecutionScope.StepLevel);
        manualHandler.Setup(h => h.ExecuteStepLevelAsync(It.IsAny<StepActionContext>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var registry = new Mock<IActionHandlerRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(manualHandler.Object);
        registry.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.StepLevel);

        var phase = new ExecuteStepsPhase(registry.Object, lifecycle.Object, new Mock<IDeploymentInterruptionService>().Object, new Mock<IDeploymentCheckpointService>().Object);

        var ctx = new DeploymentTaskContext
        {
            Deployment = new Deployment { Id = 1, SpaceId = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Id = 1, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>
            {
                new()
                {
                    Id = 1, StepOrder = 1, Name = "Approval", StepType = "Action",
                    Condition = "Success", IsDisabled = false, IsRequired = false, StartTrigger = "StartAfterPrevious",
                    Properties = new List<DeploymentStepPropertyDto>(),
                    Actions = new List<DeploymentActionDto>
                    {
                        new() { Id = 1, StepId = 1, ActionOrder = 1, Name = "Approval", ActionType = "Squid.Manual", IsDisabled = false, Properties = new List<DeploymentActionPropertyDto>(), Environments = new List<int>(), ExcludedEnvironments = new List<int>(), Channels = new List<int>() }
                    }
                }
            },
            AllTargets = new List<Machine> { new() { Id = 1, Name = "machine-1" }, new() { Id = 2, Name = "machine-2" } },
            AllTargetsContext = new List<DeploymentTargetContext>
            {
                new() { Machine = new Machine { Id = 1, Name = "machine-1", Roles = "[\"web\"]", HealthStatus = MachineHealthStatus.Healthy }, CommunicationStyle = CommunicationStyle.KubernetesApi },
                new() { Machine = new Machine { Id = 2, Name = "machine-2", Roles = "[\"web\"]", HealthStatus = MachineHealthStatus.Healthy }, CommunicationStyle = CommunicationStyle.KubernetesAgent }
            }
        };

        lifecycle.Object.Initialize(ctx);
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        emittedEvents.ShouldContain(e => e is StepStartingEvent);
        emittedEvents.ShouldContain(e => e is StepCompletedEvent);
        emittedEvents.ShouldNotContain(e => e is StepExecutingOnTargetEvent);
        emittedEvents.ShouldNotContain(e => e is StepNoMatchingTargetsEvent);
        manualHandler.Verify(h => h.ExecuteStepLevelAsync(It.IsAny<StepActionContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Step-Level Environment/Channel Filtering ==========

    [Fact]
    public async Task Pipeline_StepLevelAction_InvisibleWhenEnvironmentDoesNotMatch()
    {
        var emittedEvents = new List<DeploymentLifecycleEvent>();

        var lifecycle = new Mock<IDeploymentLifecycle>();
        lifecycle.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentLifecycleEvent, CancellationToken>((e, _) => emittedEvents.Add(e))
            .Returns(Task.CompletedTask);

        var manualHandler = new Mock<IActionHandler>();
        manualHandler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        manualHandler.Setup(h => h.ExecutionScope).Returns(ExecutionScope.StepLevel);
        manualHandler.Setup(h => h.ExecuteStepLevelAsync(It.IsAny<StepActionContext>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var registry = new Mock<IActionHandlerRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(manualHandler.Object);
        registry.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.StepLevel);

        var phase = new ExecuteStepsPhase(registry.Object, lifecycle.Object, new Mock<IDeploymentInterruptionService>().Object, new Mock<IDeploymentCheckpointService>().Object);

        // Deployment targets environment 1 (TEST), but action is configured for environment 99 (PRD)
        var ctx = new DeploymentTaskContext
        {
            Deployment = new Deployment { Id = 1, SpaceId = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Id = 1, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>
            {
                new()
                {
                    Id = 1, StepOrder = 1, Name = "Approval", StepType = "Action",
                    Condition = "Success", IsDisabled = false, IsRequired = false, StartTrigger = "StartAfterPrevious",
                    Properties = new List<DeploymentStepPropertyDto>(),
                    Actions = new List<DeploymentActionDto>
                    {
                        new() { Id = 1, StepId = 1, ActionOrder = 1, Name = "Approval", ActionType = "Squid.Manual", IsDisabled = false, Properties = new List<DeploymentActionPropertyDto>(), Environments = new List<int> { 99 }, ExcludedEnvironments = new List<int>(), Channels = new List<int>() }
                    }
                }
            },
            AllTargets = new List<Machine>(),
            AllTargetsContext = new List<DeploymentTargetContext>()
        };

        lifecycle.Object.Initialize(ctx);
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        // Step with all actions filtered by environment is completely invisible — no events emitted
        manualHandler.Verify(h => h.ExecuteStepLevelAsync(It.IsAny<StepActionContext>(), It.IsAny<CancellationToken>()), Times.Never);
        emittedEvents.ShouldNotContain(e => e is StepStartingEvent);
        emittedEvents.ShouldNotContain(e => e is StepCompletedEvent);
        emittedEvents.ShouldNotContain(e => e is ActionSkippedEvent);
    }

    [Fact]
    public async Task Pipeline_StepLevelAction_ExecutesWhenEnvironmentMatches()
    {
        var manualHandler = new Mock<IActionHandler>();
        manualHandler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        manualHandler.Setup(h => h.ExecutionScope).Returns(ExecutionScope.StepLevel);
        manualHandler.Setup(h => h.ExecuteStepLevelAsync(It.IsAny<StepActionContext>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var registry = new Mock<IActionHandlerRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(manualHandler.Object);
        registry.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.StepLevel);

        var lifecycle = new Mock<IDeploymentLifecycle>();
        lifecycle.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var phase = new ExecuteStepsPhase(registry.Object, lifecycle.Object, new Mock<IDeploymentInterruptionService>().Object, new Mock<IDeploymentCheckpointService>().Object);

        // Deployment targets environment 99 (PRD), action configured for environment 99 (PRD) — should execute
        var ctx = new DeploymentTaskContext
        {
            Deployment = new Deployment { Id = 1, SpaceId = 1, EnvironmentId = 99, ChannelId = 1 },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Id = 1, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>
            {
                new()
                {
                    Id = 1, StepOrder = 1, Name = "Approval", StepType = "Action",
                    Condition = "Success", IsDisabled = false, IsRequired = false, StartTrigger = "StartAfterPrevious",
                    Properties = new List<DeploymentStepPropertyDto>(),
                    Actions = new List<DeploymentActionDto>
                    {
                        new() { Id = 1, StepId = 1, ActionOrder = 1, Name = "Approval", ActionType = "Squid.Manual", IsDisabled = false, Properties = new List<DeploymentActionPropertyDto>(), Environments = new List<int> { 99 }, ExcludedEnvironments = new List<int>(), Channels = new List<int>() }
                    }
                }
            },
            AllTargets = new List<Machine>(),
            AllTargetsContext = new List<DeploymentTargetContext>()
        };

        lifecycle.Object.Initialize(ctx);
        await phase.ExecuteAsync(ctx, CancellationToken.None);

        manualHandler.Verify(h => h.ExecuteStepLevelAsync(It.IsAny<StepActionContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Resolved Event Emission ==========

    [Fact]
    public async Task ExecuteStepLevelAsync_ResumeWithProceed_EmitsResolvedEvent()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Proceed" });

        var lifecycle = new Mock<IDeploymentLifecycle>();
        var handler = new ManualInterventionActionHandler(interruptionService.Object, new Mock<IServerTaskService>().Object, lifecycle.Object);
        var ctx = CreateStepActionContext();

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        lifecycle.Verify(l => l.EmitAsync(It.Is<ManualInterventionResolvedEvent>(e => e.Context.GuidedFailureResolution == "Proceed"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_ResumeWithAbort_EmitsResolvedEvent()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Abort" });

        var lifecycle = new Mock<IDeploymentLifecycle>();
        var handler = new ManualInterventionActionHandler(interruptionService.Object, new Mock<IServerTaskService>().Object, lifecycle.Object);
        var ctx = CreateStepActionContext();

        await Should.ThrowAsync<DeploymentAbortedException>(() => handler.ExecuteStepLevelAsync(ctx, CancellationToken.None));

        lifecycle.Verify(l => l.EmitAsync(It.Is<ManualInterventionResolvedEvent>(e => e.Context.GuidedFailureResolution == "Abort"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Helpers ==========

    private static ManualInterventionActionHandler CreateHandler()
    {
        return new ManualInterventionActionHandler(
            new Mock<IDeploymentInterruptionService>().Object,
            new Mock<IServerTaskService>().Object,
            new Mock<IDeploymentLifecycle>().Object);
    }

    private static StepActionContext CreateStepActionContext()
    {
        return new StepActionContext
        {
            ServerTaskId = 1, DeploymentId = 1, SpaceId = 1,
            Step = new DeploymentStepDto { Id = 1, Name = "Step 1" },
            Action = new DeploymentActionDto
            {
                Id = 1, Name = "Manual Action", ActionType = "Squid.Manual",
                Properties = new List<DeploymentActionPropertyDto>
                {
                    new() { PropertyName = "Squid.Action.Manual.Instructions", PropertyValue = "Please verify" }
                }
            },
            Variables = new List<VariableDto>(),
            ReleaseVersion = "1.0.0",
            StepDisplayOrder = 1, ActionSortOrder = 1
        };
    }
}
