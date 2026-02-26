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
        var selectedPackage = selectedPackages?
            .FirstOrDefault(sp => string.Equals(sp.ActionName, action.Name, StringComparison.OrdinalIgnoreCase));

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
}
