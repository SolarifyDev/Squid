using Squid.Core.Services.Deployments.Runbooks.Exceptions;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.Deployments.Runbooks;

internal static class RunbookPublicationValidator
{
    private static readonly HashSet<string> SupportedStepProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        SpecialVariables.Step.TargetRoles, "TargetRoles", SpecialVariables.Step.RunOnServer
    };

    private static readonly HashSet<string> SupportedActionProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        SpecialVariables.Action.ScriptBody, SpecialVariables.Action.ScriptSyntax, SpecialVariables.Action.ScriptSource
    };

    public static void EnsureSupported(DeploymentProcessSnapshotDataDto draft)
    {
        var steps = draft.StepSnapshots.Where(s => !s.IsDisabled).ToList();
        if (steps.Count == 0 || steps.All(s => s.ActionSnapshots.All(a => a.IsDisabled)))
            throw Invalid("Add at least one enabled inline Bash script.");

        foreach (var step in steps)
        {
            if (!IsDefaultOr(step.StepType, "Action", SpecialVariables.StepTypes.RunScript)
                || !IsDefaultOr(step.Condition, "Success")
                || !IsDefaultOr(step.StartTrigger, SpecialVariables.StartTriggers.StartAfterPrevious)
                || !IsDefaultOr(step.PackageRequirement, SpecialVariables.PackageRequirements.LetSquidDecide)
                || !step.IsRequired
                || !IsDefaultOr(step.Properties.GetValueOrDefault(SpecialVariables.Step.RunOnServer), "false"))
            {
                throw Invalid($"Step '{step.Name}' must be a required, sequential script on a deployment target, with no package dependency.");
            }

            ValidateProperties(step.Properties, SupportedStepProperties, step.Name);

            foreach (var action in step.ActionSnapshots.Where(a => !a.IsDisabled))
            {
                if (!string.Equals(action.ActionType, SpecialVariables.ActionTypes.Script, StringComparison.OrdinalIgnoreCase)
                    || !IsDefaultOr(action.Properties.GetValueOrDefault(SpecialVariables.Action.ScriptSource), "Inline")
                    || !string.Equals(action.Properties.GetValueOrDefault(SpecialVariables.Action.ScriptSyntax),
                        SpecialVariables.ScriptSyntax.Bash, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(action.Properties.GetValueOrDefault(SpecialVariables.Action.ScriptBody)))
                {
                    throw Invalid($"Action '{action.Name}' must have a non-empty inline Bash script.");
                }

                if (action.WorkerPoolId.HasValue || action.Channels.Count != 0
                    || !action.IsRequired || action.CanBeUsedForProjectVersioning)
                {
                    throw Invalid($"Action '{action.Name}' uses unsupported deployment settings.");
                }

                ValidateProperties(action.Properties, SupportedActionProperties, action.Name);
            }
        }
    }

    private static void ValidateProperties(Dictionary<string, string> properties, HashSet<string> supported, string name)
    {
        if (properties.Keys.Any(key => !supported.Contains(key)))
            throw Invalid($"'{name}' uses properties outside the supported inline Bash configuration.");

        if (properties.Values.Any(value => value?.Contains("#{", StringComparison.Ordinal) == true))
            throw Invalid($"'{name}' uses Squid variable expressions, which are not supported in published Runbooks yet.");
    }

    private static bool IsDefaultOr(string value, params string[] allowed) =>
        string.IsNullOrEmpty(value) || allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static RunbookValidationException Invalid(string message) =>
        new("runbook.unsupported_draft", "ProcessId", message);
}
