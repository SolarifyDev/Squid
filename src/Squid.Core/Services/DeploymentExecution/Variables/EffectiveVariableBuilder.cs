using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

public static class EffectiveVariableBuilder
{
    public static List<VariableDto> BuildEffectiveVariables(
        List<VariableDto> baseVariables, DeploymentTargetContext target, VariableScopeContext scopeContext)
    {
        var scopedVariables = VariableScopeEvaluator.Evaluate(baseVariables, scopeContext);

        var variables = new List<VariableDto>(scopedVariables);
        variables.AddRange(target.EndpointVariables);

        return variables;
    }

    public static List<VariableDto> BuildActionVariables(List<VariableDto> effectiveVariables, DeploymentActionDto action, IEnumerable<Persistence.Entities.Deployments.ReleaseSelectedPackage> selectedPackages)
    {
        var variables = new List<VariableDto>(effectiveVariables ?? new List<VariableDto>());

        // Promote action properties into the variable set so Calamari / agent-side
        // steps can read install options and feature flags by the same names that
        // operators configure on the step editor.
        // Caller MUST pass already-expanded action properties (see ExecuteStepsPhase.Prepare).
        if (action?.Properties != null)
        {
            foreach (var property in action.Properties)
            {
                if (string.IsNullOrWhiteSpace(property?.PropertyName))
                    continue;

                variables.RemoveAll(v => string.Equals(v.Name, property.PropertyName, StringComparison.OrdinalIgnoreCase));
                variables.Add(new VariableDto
                {
                    Name = property.PropertyName,
                    Value = property.PropertyValue ?? string.Empty
                });
            }
        }

        var selectedPackage = FindPrimaryPackage(selectedPackages, action?.Name);

        if (selectedPackage != null)
        {
            variables.RemoveAll(v => string.Equals(v.Name, SpecialVariables.Action.PackageVersion, StringComparison.OrdinalIgnoreCase));
            variables.Add(new VariableDto
            {
                Name = SpecialVariables.Action.PackageVersion,
                Value = selectedPackage.Version
            });
        }

        return variables;
    }

    private static Persistence.Entities.Deployments.ReleaseSelectedPackage FindPrimaryPackage(
        IEnumerable<Persistence.Entities.Deployments.ReleaseSelectedPackage> selectedPackages, string actionName)
    {
        if (selectedPackages == null) return null;

        Persistence.Entities.Deployments.ReleaseSelectedPackage firstMatch = null;

        foreach (var sp in selectedPackages)
        {
            if (!string.Equals(sp.ActionName, actionName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrEmpty(sp.PackageReferenceName))
                return sp;

            firstMatch ??= sp;
        }

        return firstMatch;
    }
}
