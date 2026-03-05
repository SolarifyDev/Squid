using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

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
        var selectedPackage = FindPrimaryPackage(selectedPackages, action.Name);

        if (selectedPackage == null)
            return effectiveVariables;

        var variables = new List<VariableDto>(effectiveVariables)
        {
            new()
            {
                Name = SpecialVariables.Action.PackageVersion,
                Value = selectedPackage.Version
            }
        };

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
