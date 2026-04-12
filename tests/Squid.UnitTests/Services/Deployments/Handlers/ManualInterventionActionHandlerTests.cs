using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
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
        var action = new DeploymentActionDto { ActionType = "Squid.Script" };

        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        ((IActionHandler)_handler).CanHandle(null).ShouldBeFalse();
    }

}
