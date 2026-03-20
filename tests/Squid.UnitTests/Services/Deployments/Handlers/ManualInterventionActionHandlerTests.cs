using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Handlers;

public class ManualInterventionActionHandlerTests
{
    private readonly Mock<IDeploymentInterruptionService> _interruptionService = new();
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<IDeploymentLifecycle> _lifecycle = new();
    private readonly ManualInterventionActionHandler _handler;

    public ManualInterventionActionHandlerTests()
    {
        _handler = new ManualInterventionActionHandler(_interruptionService.Object, _serverTaskService.Object, _lifecycle.Object);
    }

    [Fact]
    public void ActionType_IsManual()
    {
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.Manual);
    }

    [Fact]
    public void ExecutionScope_IsStepLevel()
    {
        _handler.ExecutionScope.ShouldBe(ExecutionScope.StepLevel);
    }

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.Manual" };

        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesRunScript" };

        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        ((IActionHandler)_handler).CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareAsync_ReturnsManualInterventionExecutionMode()
    {
        var ctx = BuildContext();

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.ManualIntervention);
    }

    [Fact]
    public async Task PrepareAsync_ExtractsInstructionsFromActionProperties()
    {
        var instructions = "Please approve this deployment before continuing.";
        var ctx = BuildContext(instructions);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ManualInterventionInstructions.ShouldBe(instructions);
    }

    [Fact]
    public async Task PrepareAsync_MissingInstructions_DefaultsToEmptyString()
    {
        var ctx = BuildContext();

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ManualInterventionInstructions.ShouldBe("");
    }

    [Fact]
    public async Task PrepareAsync_ContextPreparationPolicy_IsSkip()
    {
        var ctx = BuildContext();

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
    }

    // === Helpers ===

    private static ActionExecutionContext BuildContext(string instructions = null)
    {
        var properties = new List<DeploymentActionPropertyDto>();

        if (instructions != null)
            properties.Add(new DeploymentActionPropertyDto { PropertyName = "Squid.Action.Manual.Instructions", PropertyValue = instructions });

        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = "Manual Step" },
            Action = new DeploymentActionDto
            {
                Name = "Manual Intervention",
                ActionType = "Squid.Manual",
                Properties = properties
            }
        };
    }
}
