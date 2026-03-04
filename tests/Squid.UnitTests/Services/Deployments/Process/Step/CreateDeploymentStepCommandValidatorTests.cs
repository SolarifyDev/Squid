using Squid.Core.Validators.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Process.Step;

public class CreateDeploymentStepCommandValidatorTests
{
    private readonly CreateDeploymentStepCommandValidator _validator = new();

    [Fact]
    public void Valid_Command_Passes()
    {
        var command = ValidCommand();

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_ProcessId_Fails(int processId)
    {
        var command = ValidCommand();
        command.ProcessId = processId;

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "ProcessId");
    }

    [Fact]
    public void Null_Step_Fails()
    {
        var command = ValidCommand();
        command.Step = null;

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Step");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Step_Name_Fails(string name)
    {
        var command = ValidCommand();
        command.Step.Name = name;

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Step.Name");
    }

    [Fact]
    public void Step_Name_Exceeds_MaxLength_Fails()
    {
        var command = ValidCommand();
        command.Step.Name = new string('a', 201);

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Step.Name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Action_With_Empty_ActionType_Fails(string actionType)
    {
        var command = ValidCommand();
        command.Step.Actions.Add(new CreateOrUpdateDeploymentActionModel { ActionType = actionType, Name = "bad-action" });

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.Contains("ActionType"));
    }

    [Fact]
    public void No_Actions_Passes()
    {
        var command = ValidCommand();
        command.Step.Actions.Clear();

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeTrue();
    }

    private static CreateDeploymentStepCommand ValidCommand() => new()
    {
        ProcessId = 1,
        Step = new CreateOrUpdateDeploymentStepModel
        {
            Name = "Deploy Step",
            Actions = [new() { ActionType = "Squid.KubernetesDeployContainers", Name = "deploy" }]
        }
    };
}
