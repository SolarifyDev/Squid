using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

public static class PromptedVariableMerger
{
    public static void MergePromptedValues(List<VariableDto> variables, Dictionary<string, string> formValues)
    {
        if (formValues == null || formValues.Count == 0) return;

        foreach (var variable in variables)
        {
            if (!variable.IsPrompted) continue;

            if (formValues.TryGetValue(variable.Name, out var overrideValue))
                variable.Value = overrideValue;
        }
    }

    public static List<string> ValidateRequiredPrompts(List<VariableDto> variables, Dictionary<string, string> formValues)
    {
        var errors = new List<string>();

        foreach (var variable in variables)
        {
            if (!variable.IsPrompted) continue;
            if (!variable.PromptRequired) continue;

            var hasFormValue = formValues != null && formValues.TryGetValue(variable.Name, out var formValue) && !string.IsNullOrEmpty(formValue);
            var hasDefaultValue = !string.IsNullOrEmpty(variable.Value);

            if (!hasFormValue && !hasDefaultValue)
                errors.Add($"Required prompted variable \"{variable.PromptLabel}\" ({variable.Name}) has no value provided.");
        }

        return errors;
    }
}
