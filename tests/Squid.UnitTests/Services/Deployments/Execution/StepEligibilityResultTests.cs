using System;
using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class StepEligibilityResultTests
{
    // ========== EvaluateStep — Individual Reasons ==========

    [Fact]
    public void EvaluateStep_Disabled_ReturnsDisabledReason()
    {
        var step = MakeStep(isDisabled: true);

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: true);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(StepSkipReason.Disabled);
        result.Message.ShouldContain("disabled", Case.Insensitive);
    }

    [Fact]
    public void EvaluateStep_SuccessCondition_PreviousFailed_ReturnsSuccessConditionNotMet()
    {
        var step = MakeStep(condition: "Success");

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: false);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(StepSkipReason.SuccessConditionNotMet);
        result.Message.ShouldContain("previous step failed", Case.Insensitive);
    }

    [Fact]
    public void EvaluateStep_FailureCondition_PreviousSucceeded_ReturnsFailureConditionNotMet()
    {
        var step = MakeStep(condition: "Failure");

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: true);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(StepSkipReason.FailureConditionNotMet);
        result.Message.ShouldContain("no previous step has failed", Case.Insensitive);
    }

    [Fact]
    public void EvaluateStep_VariableCondition_EvalFalse_ReturnsVariableConditionFalse()
    {
        var step = MakeStepWithVariableCondition("#{IsProduction}");
        var variables = new List<VariableDto> { new() { Name = "IsProduction", Value = "False" } };

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: true, variables);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(StepSkipReason.VariableConditionFalse);
        result.Message.ShouldContain("variable", Case.Insensitive);
    }

    [Fact]
    public void EvaluateStep_RoleMismatch_ReturnsRoleMismatchReason()
    {
        var step = MakeStep(targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database" };

        var result = StepEligibilityEvaluator.EvaluateStep(step, machineRoles, previousStepSucceeded: true);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(StepSkipReason.RoleMismatch);
        result.Message.ShouldContain("roles", Case.Insensitive);
    }

    [Fact]
    public void EvaluateStep_AllMet_ReturnsExecute()
    {
        var step = MakeStep(targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = StepEligibilityEvaluator.EvaluateStep(step, machineRoles, previousStepSucceeded: true);

        result.ShouldExecute.ShouldBeTrue();
        result.SkipReason.ShouldBe(StepSkipReason.None);
    }

    // ========== EvaluateStep — Priority Order ==========

    [Theory]
    [InlineData("Success", true)]
    [InlineData("Failure", false)]
    [InlineData("Always", true)]
    public void EvaluateStep_DisabledCheckedFirst(string condition, bool previousSucceeded)
    {
        var step = MakeStep(isDisabled: true, condition: condition, targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = StepEligibilityEvaluator.EvaluateStep(step, machineRoles, previousStepSucceeded: previousSucceeded);

        result.SkipReason.ShouldBe(StepSkipReason.Disabled);
    }

    [Fact]
    public void EvaluateStep_ConditionCheckedBeforeRoles()
    {
        var step = MakeStep(condition: "Success", targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = StepEligibilityEvaluator.EvaluateStep(step, machineRoles, previousStepSucceeded: false);

        result.SkipReason.ShouldBe(StepSkipReason.SuccessConditionNotMet);
    }

    // ========== EvaluateAction ==========

    [Fact]
    public void EvaluateAction_Disabled_ReturnsDisabledReason()
    {
        var action = MakeAction(isDisabled: true);

        var result = StepEligibilityEvaluator.EvaluateAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(ActionSkipReason.Disabled);
        result.Message.ShouldContain("disabled", Case.Insensitive);
    }

    [Fact]
    public void EvaluateAction_EnvironmentMismatch_ReturnsEnvironmentMismatchReason()
    {
        var action = MakeAction(environments: new List<int> { 1, 2 });

        var result = StepEligibilityEvaluator.EvaluateAction(action, deploymentEnvironmentId: 99, deploymentChannelId: 1);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(ActionSkipReason.EnvironmentMismatch);
        result.Message.ShouldContain("environment", Case.Insensitive);
    }

    [Fact]
    public void EvaluateAction_ExcludedEnvironment_ReturnsEnvironmentMismatchReason()
    {
        var action = MakeAction(excludedEnvironments: new List<int> { 5 });

        var result = StepEligibilityEvaluator.EvaluateAction(action, deploymentEnvironmentId: 5, deploymentChannelId: 1);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(ActionSkipReason.EnvironmentMismatch);
        result.Message.ShouldContain("environment", Case.Insensitive);
    }

    [Fact]
    public void EvaluateAction_ChannelMismatch_ReturnsChannelMismatchReason()
    {
        var action = MakeAction(channels: new List<int> { 10 });

        var result = StepEligibilityEvaluator.EvaluateAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 99);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(ActionSkipReason.ChannelMismatch);
        result.Message.ShouldContain("channel", Case.Insensitive);
    }

    [Fact]
    public void EvaluateAction_AllMet_ReturnsExecute()
    {
        var action = MakeAction(environments: new List<int> { 1 }, channels: new List<int> { 10 });

        var result = StepEligibilityEvaluator.EvaluateAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 10);

        result.ShouldExecute.ShouldBeTrue();
        result.SkipReason.ShouldBe(ActionSkipReason.None);
    }

    [Fact]
    public void EvaluateAction_ManuallySkipped_ReturnsManuallySkippedReason()
    {
        var action = MakeAction();
        var ctx = new ActionEvaluationContext(1, 1, new HashSet<int> { action.Id });

        var result = StepEligibilityEvaluator.EvaluateAction(action, ctx);

        result.ShouldExecute.ShouldBeFalse();
        result.SkipReason.ShouldBe(ActionSkipReason.ManuallySkipped);
        result.Message.ShouldContain("manually excluded", Case.Insensitive);
    }

    [Fact]
    public void EvaluateAction_ManuallySkipped_TakesPriorityOverDisabled()
    {
        var action = MakeAction(isDisabled: true);
        var ctx = new ActionEvaluationContext(1, 1, new HashSet<int> { action.Id });

        var result = StepEligibilityEvaluator.EvaluateAction(action, ctx);

        result.SkipReason.ShouldBe(ActionSkipReason.ManuallySkipped);
    }

    // ========== Condition-Met Messages ==========

    [Fact]
    public void EvaluateStep_FailureConditionMet_ReturnsConditionMetMessage()
    {
        var step = MakeStep(condition: "Failure");

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: false);

        result.ShouldExecute.ShouldBeTrue();
        result.SkipReason.ShouldBe(StepSkipReason.None);
        result.Message.ShouldContain("failure has been detected", Case.Insensitive);
        result.Message.ShouldContain(step.Name);
    }

    [Fact]
    public void EvaluateStep_VariableConditionMet_ReturnsConditionMetMessage()
    {
        var step = MakeStepWithVariableCondition("#{IsProduction}");
        var variables = new List<VariableDto> { new() { Name = "IsProduction", Value = "True" } };

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: true, variables);

        result.ShouldExecute.ShouldBeTrue();
        result.SkipReason.ShouldBe(StepSkipReason.None);
        result.Message.ShouldContain("Variable run condition", Case.Insensitive);
        result.Message.ShouldContain(step.Name);
    }

    [Fact]
    public void EvaluateStep_SuccessConditionMet_ReturnsNoMessage()
    {
        var step = MakeStep(condition: "Success");

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: true);

        result.ShouldExecute.ShouldBeTrue();
        result.SkipReason.ShouldBe(StepSkipReason.None);
        result.Message.ShouldBeNull();
    }

    [Fact]
    public void EvaluateStep_AlwaysCondition_ExecutesWithNoMessage()
    {
        var step = MakeStep(condition: "Always");

        var result = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: false);

        result.ShouldExecute.ShouldBeTrue();
        result.SkipReason.ShouldBe(StepSkipReason.None);
        result.Message.ShouldBeNull();
    }

    // ========== Backwards Compatibility ==========

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldExecuteStep_BackwardsCompat(bool isDisabled, bool expected)
    {
        var step = MakeStep(isDisabled: isDisabled);

        StepEligibilityEvaluator.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldExecuteAction_BackwardsCompat(bool isDisabled, bool expected)
    {
        var action = MakeAction(isDisabled: isDisabled);

        StepEligibilityEvaluator.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBe(expected);
    }

    // ========== Helpers ==========

    private static DeploymentStepDto MakeStep(bool isDisabled = false, string condition = "Success", string targetRoles = null)
    {
        var step = new DeploymentStepDto
        {
            Id = 1, StepOrder = 1, Name = "Test Step", StepType = "Action",
            Condition = condition, IsDisabled = isDisabled, IsRequired = true,
            Properties = new List<DeploymentStepPropertyDto>()
        };

        if (targetRoles != null)
        {
            step.Properties.Add(new DeploymentStepPropertyDto
            {
                StepId = 1,
                PropertyName = DeploymentVariables.Action.TargetRoles,
                PropertyValue = targetRoles
            });
        }

        return step;
    }

    private static DeploymentStepDto MakeStepWithVariableCondition(string expression)
    {
        var step = MakeStep(condition: "Variable");

        step.Properties.Add(new DeploymentStepPropertyDto
        {
            StepId = 1,
            PropertyName = SpecialVariables.Step.ConditionExpression,
            PropertyValue = expression
        });

        return step;
    }

    private static DeploymentActionDto MakeAction(
        bool isDisabled = false,
        List<int> environments = null,
        List<int> excludedEnvironments = null,
        List<int> channels = null)
    {
        return new DeploymentActionDto
        {
            Id = 1, StepId = 1, ActionOrder = 1, Name = "Test Action",
            ActionType = "Octopus.Script", IsDisabled = isDisabled,
            Environments = environments ?? new List<int>(),
            ExcludedEnvironments = excludedEnvironments ?? new List<int>(),
            Channels = channels ?? new List<int>()
        };
    }
}
