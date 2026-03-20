using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class TargetStepMatcher
{
    public static List<DeploymentTargetContext> FindMatchingTargetsForStep(DeploymentStepDto step, List<DeploymentTargetContext> allTargets)
    {
        var stepRolesProperty = step.Properties?
            .FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.TargetRoles);

        if (stepRolesProperty == null || string.IsNullOrEmpty(stepRolesProperty.PropertyValue))
            return allTargets;

        var stepRoles = DeploymentTargetFinder.ParseCsvRoles(stepRolesProperty.PropertyValue);

        return allTargets
            .Where(tc => DeploymentTargetFinder.ParseRoles(tc.Machine.Roles).Overlaps(stepRoles))
            .ToList();
    }
}
