using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class RunOnServerEvaluatorTests
{
    // ========== IsRunOnServer ==========

    [Fact]
    public void IsRunOnServer_PropertyTrue_ReturnsTrue()
    {
        var step = MakeStep(runOnServer: "true");

        RunOnServerEvaluator.IsRunOnServer(step).ShouldBeTrue();
    }

    [Fact]
    public void IsRunOnServer_PropertyFalse_ReturnsFalse()
    {
        var step = MakeStep(runOnServer: "false");

        RunOnServerEvaluator.IsRunOnServer(step).ShouldBeFalse();
    }

    [Fact]
    public void IsRunOnServer_PropertyMissing_ReturnsFalse()
    {
        var step = MakeStep();

        RunOnServerEvaluator.IsRunOnServer(step).ShouldBeFalse();
    }

    [Theory]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("true")]
    [InlineData("tRuE")]
    public void IsRunOnServer_CaseInsensitive_ReturnsTrue(string value)
    {
        var step = MakeStep(runOnServer: value);

        RunOnServerEvaluator.IsRunOnServer(step).ShouldBeTrue();
    }

    // ========== IsEntireDeploymentServerOnly ==========

    [Fact]
    public void IsEntireDeploymentServerOnly_AllRunOnServer_ReturnsTrue()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(runOnServer: "true"),
            MakeStep(runOnServer: "true")
        };

        RunOnServerEvaluator.IsEntireDeploymentServerOnly(steps, _ => ExecutionScope.TargetLevel).ShouldBeTrue();
    }

    [Fact]
    public void IsEntireDeploymentServerOnly_MixedSteps_ReturnsFalse()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(runOnServer: "true"),
            MakeStep()
        };

        RunOnServerEvaluator.IsEntireDeploymentServerOnly(steps, _ => ExecutionScope.TargetLevel).ShouldBeFalse();
    }

    [Fact]
    public void IsEntireDeploymentServerOnly_AllStepLevel_ReturnsTrue()
    {
        var step = MakeStep();
        step.Actions = new List<DeploymentActionDto>
        {
            new() { Id = 1, Name = "Manual", ActionType = "Squid.Manual", IsDisabled = false }
        };

        var steps = new List<DeploymentStepDto> { step };

        RunOnServerEvaluator.IsEntireDeploymentServerOnly(steps, _ => ExecutionScope.StepLevel).ShouldBeTrue();
    }

    [Fact]
    public void IsEntireDeploymentServerOnly_MixedRunOnServerAndStepLevel_ReturnsTrue()
    {
        var runOnServerStep = MakeStep(runOnServer: "true");

        var stepLevelStep = MakeStep();
        stepLevelStep.Actions = new List<DeploymentActionDto>
        {
            new() { Id = 1, Name = "Manual", ActionType = "Squid.Manual", IsDisabled = false }
        };

        var steps = new List<DeploymentStepDto> { runOnServerStep, stepLevelStep };

        RunOnServerEvaluator.IsEntireDeploymentServerOnly(steps, _ => ExecutionScope.StepLevel).ShouldBeTrue();
    }

    [Fact]
    public void IsEntireDeploymentServerOnly_EmptySteps_ReturnsTrue()
    {
        RunOnServerEvaluator.IsEntireDeploymentServerOnly(new List<DeploymentStepDto>(), _ => ExecutionScope.TargetLevel).ShouldBeTrue();
    }

    [Fact]
    public void IsEntireDeploymentServerOnly_NullSteps_ReturnsTrue()
    {
        RunOnServerEvaluator.IsEntireDeploymentServerOnly(null, _ => ExecutionScope.TargetLevel).ShouldBeTrue();
    }

    [Fact]
    public void IsEntireDeploymentServerOnly_DisabledTargetStep_Ignored()
    {
        var runOnServerStep = MakeStep(runOnServer: "true");
        var disabledTargetStep = MakeStep(isDisabled: true);

        var steps = new List<DeploymentStepDto> { runOnServerStep, disabledTargetStep };

        RunOnServerEvaluator.IsEntireDeploymentServerOnly(steps, _ => ExecutionScope.TargetLevel).ShouldBeTrue();
    }

    // ========== Helpers ==========

    private static DeploymentStepDto MakeStep(string runOnServer = null, bool isDisabled = false)
    {
        var step = new DeploymentStepDto
        {
            Id = 1, StepOrder = 1, Name = "Test Step", StepType = "Action",
            Condition = "Success", IsDisabled = isDisabled, IsRequired = true,
            Properties = new List<DeploymentStepPropertyDto>(),
            Actions = new List<DeploymentActionDto>()
        };

        if (runOnServer != null)
        {
            step.Properties.Add(new DeploymentStepPropertyDto
            {
                StepId = 1, PropertyName = SpecialVariables.Step.RunOnServer, PropertyValue = runOnServer
            });
        }

        return step;
    }
}
