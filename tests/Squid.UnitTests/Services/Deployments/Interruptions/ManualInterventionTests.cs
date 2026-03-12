using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
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
