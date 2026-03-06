using Squid.Core.Validators.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Deployment;

namespace Squid.UnitTests.Validators.Deployments.DeploymentCommands;

public class CreateDeploymentCommandValidatorTests
{
    private readonly CreateDeploymentCommandValidator _validator = new();

    [Fact]
    public void Valid_Command_Passes()
    {
        var command = new CreateDeploymentCommand
        {
            ReleaseId = 1,
            EnvironmentId = 1,
            DeployedBy = 1,
            Name = "Deploy 1.0.0"
        };

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_ReleaseId_Fails(int releaseId)
    {
        var command = ValidCommand();
        command.ReleaseId = releaseId;

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "ReleaseId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_EnvironmentId_Fails(int environmentId)
    {
        var command = ValidCommand();
        command.EnvironmentId = environmentId;

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "EnvironmentId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_DeployedBy_Fails(int deployedBy)
    {
        var command = ValidCommand();
        command.DeployedBy = deployedBy;

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "DeployedBy");
    }

    [Fact]
    public void Name_ExceedsMaxLength_Fails()
    {
        var command = ValidCommand();
        command.Name = new string('a', 201);

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Name");
    }

    [Fact]
    public void SpecificMachineIds_WithInvalidValue_Fails()
    {
        var command = ValidCommand();
        command.SpecificMachineIds = ["abc"];

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.Contains("SpecificMachineIds"));
    }

    [Fact]
    public void ExcludedMachineIds_WithInvalidValue_Fails()
    {
        var command = ValidCommand();
        command.ExcludedMachineIds = ["abc"];

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.Contains("ExcludedMachineIds"));
    }

    [Fact]
    public void Overlapping_SpecificAndExcludedMachineIds_Fails()
    {
        var command = ValidCommand();
        command.SpecificMachineIds = ["1", "2"];
        command.ExcludedMachineIds = ["2", "3"];

        var result = _validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("cannot overlap"));
    }

    private static CreateDeploymentCommand ValidCommand() => new()
    {
        ReleaseId = 1,
        EnvironmentId = 1,
        DeployedBy = 1,
        Name = "Deploy 1.0.0"
    };
}
