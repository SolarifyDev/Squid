using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public static class StepEligibilityEvaluator
{
    private static readonly ActionEligibilityResult ExecuteAction = new(true, ActionSkipReason.None, null);

    public static bool ShouldExecuteStep(DeploymentStepDto step, HashSet<string> targetRoles, bool previousStepSucceeded, List<VariableDto> effectiveVariables = null)
        => EvaluateStep(step, targetRoles, previousStepSucceeded, effectiveVariables).ShouldExecute;

    public static bool ShouldExecuteAction(DeploymentActionDto action, int deploymentEnvironmentId, int deploymentChannelId)
        => EvaluateAction(action, deploymentEnvironmentId, deploymentChannelId).ShouldExecute;

    public static StepEligibilityResult EvaluateStep(DeploymentStepDto step, HashSet<string> targetRoles, bool previousStepSucceeded, List<VariableDto> effectiveVariables = null)
    {
        if (step.IsDisabled)
            return new StepEligibilityResult(false, StepSkipReason.Disabled, $"Step \"{step.Name}\" is disabled");

        var conditionResult = ResolveConditionSkipReason(step, previousStepSucceeded, effectiveVariables);

        if (conditionResult.HasValue)
            return new StepEligibilityResult(false, conditionResult.Value, BuildConditionSkipMessage(step.Name, conditionResult.Value));

        if (!MatchesTargetRoles(step, targetRoles))
            return new StepEligibilityResult(false, StepSkipReason.RoleMismatch, $"Step \"{step.Name}\" target roles do not match machine roles");

        var conditionMetMessage = BuildConditionMetMessage(step.Name, step.Condition);

        return new StepEligibilityResult(true, StepSkipReason.None, conditionMetMessage);
    }

    public static ActionEligibilityResult EvaluateAction(DeploymentActionDto action, int deploymentEnvironmentId, int deploymentChannelId)
    {
        if (action.IsDisabled)
            return new ActionEligibilityResult(false, ActionSkipReason.Disabled, $"Action \"{action.Name}\" is disabled");

        if (!AppliesToEnvironment(action, deploymentEnvironmentId))
            return new ActionEligibilityResult(false, ActionSkipReason.EnvironmentMismatch, $"Action \"{action.Name}\" does not apply to current environment");

        if (action.Channels != null && action.Channels.Count > 0 && !action.Channels.Contains(deploymentChannelId))
            return new ActionEligibilityResult(false, ActionSkipReason.ChannelMismatch, $"Action \"{action.Name}\" does not apply to current channel");

        return ExecuteAction;
    }

    private static StepSkipReason? ResolveConditionSkipReason(DeploymentStepDto step, bool previousStepSucceeded, List<VariableDto> effectiveVariables)
    {
        return step.Condition switch
        {
            "Always" => null,
            "Failure" => previousStepSucceeded ? StepSkipReason.FailureConditionNotMet : null,
            "Variable" => EvaluateVariableCondition(step, effectiveVariables) ? null : StepSkipReason.VariableConditionFalse,
            null or "" => previousStepSucceeded ? null : StepSkipReason.SuccessConditionNotMet,
            _ => previousStepSucceeded ? null : StepSkipReason.SuccessConditionNotMet
        };
    }

    private static string BuildConditionMetMessage(string stepName, string condition)
    {
        return condition switch
        {
            "Failure" => $"A failure has been detected so \"{stepName}\" will be run.",
            "Variable" => $"Variable run condition was evaluated as true, so \"{stepName}\" will be run.",
            _ => null
        };
    }

    private static string BuildConditionSkipMessage(string stepName, StepSkipReason reason)
    {
        return reason switch
        {
            StepSkipReason.SuccessConditionNotMet => $"Skipping step \"{stepName}\": a previous step failed",
            StepSkipReason.FailureConditionNotMet => $"Skipping step \"{stepName}\": no previous step has failed",
            StepSkipReason.VariableConditionFalse => $"Skipping step \"{stepName}\": variable run condition evaluated to false",
            _ => $"Skipping step \"{stepName}\": condition not met"
        };
    }

    private static bool AppliesToEnvironment(DeploymentActionDto action, int environmentId)
    {
        var hasInclusion = action.Environments != null && action.Environments.Count > 0;
        var hasExclusion = action.ExcludedEnvironments != null && action.ExcludedEnvironments.Count > 0;

        if (!hasInclusion && !hasExclusion)
            return true;

        if (hasExclusion && action.ExcludedEnvironments.Contains(environmentId))
            return false;

        if (hasInclusion && !action.Environments.Contains(environmentId))
            return false;

        return true;
    }

    private static bool EvaluateVariableCondition(DeploymentStepDto step, List<VariableDto> effectiveVariables)
    {
        var expressionProperty = step.Properties?
            .FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.ConditionExpression);

        if (expressionProperty == null || string.IsNullOrWhiteSpace(expressionProperty.PropertyValue))
            return true;

        if (effectiveVariables == null)
            return true;

        var variableDictionary = VariableDictionaryFactory.Create(effectiveVariables);

        return variableDictionary.EvaluateTruthy(expressionProperty.PropertyValue);
    }

    private static bool MatchesTargetRoles(DeploymentStepDto step, HashSet<string> targetRoles)
    {
        if (targetRoles == null)
            return true;

        var stepRolesProperty = step.Properties?
            .FirstOrDefault(p => p.PropertyName == DeploymentVariables.Action.TargetRoles);

        if (stepRolesProperty == null || string.IsNullOrEmpty(stepRolesProperty.PropertyValue))
            return true;

        var stepRoles = DeploymentTargetFinder.ParseCsvRoles(stepRolesProperty.PropertyValue);

        return stepRoles.Overlaps(targetRoles);
    }
}
