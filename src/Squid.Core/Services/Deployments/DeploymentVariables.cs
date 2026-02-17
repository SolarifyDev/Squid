namespace Squid.Core.Services.Deployments;

/// <summary>
/// Centralized deployment variable and property key constants.
/// All property keys that appear in step/action Properties dictionaries
/// or in variable naming conventions must be defined here — no magic strings elsewhere.
/// </summary>
public static class DeploymentVariables
{
    public static class Action
    {
        /// <summary>
        /// Comma-separated list of target roles for a step.
        /// Stored in DeploymentStepProperty. Used for per-step machine filtering (OR logic).
        /// </summary>
        public const string TargetRoles = "Squid.Action.TargetRoles";

        /// <summary>
        /// Builds the qualified output variable name for a step action.
        /// Convention: Squid.Action[StepName].Output.VariableName
        /// </summary>
        public static string OutputVariable(string stepName, string variableName)
            => $"Squid.Action[{stepName}].Output.{variableName}";
    }
}
