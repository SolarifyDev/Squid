using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class PackageAcquisitionInjector
{
    public static List<DeploymentStepDto> InjectAcquisitionSteps(List<DeploymentStepDto> steps, List<ReleaseSelectedPackage> selectedPackages)
    {
        if (selectedPackages == null || selectedPackages.Count == 0)
            return steps;

        var packageActionNames = selectedPackages.Select(p => p.ActionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<DeploymentStepDto>();
        var acquireInjected = false;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            if (IsPreAcquisitionStep(step))
            {
                result.Add(step);
                continue;
            }

            if (!acquireInjected && StepReferencesPackages(step, packageActionNames))
            {
                result.Add(BuildAcquirePackagesStep());
                acquireInjected = true;
            }

            result.Add(step);

            if (acquireInjected && IsBlockingStep(step))
            {
                var hasPackageStepsAfter = steps.Skip(i + 1).Any(s => StepReferencesPackages(s, packageActionNames));

                if (hasPackageStepsAfter)
                    acquireInjected = false;
            }
        }

        return result;
    }

    private static bool IsPreAcquisitionStep(DeploymentStepDto step)
    {
        return string.Equals(step.PackageRequirement, SpecialVariables.PackageRequirements.BeforePackageAcquisition, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockingStep(DeploymentStepDto step)
    {
        return step.Actions.Any(a =>
            string.Equals(a.ActionType, SpecialVariables.ActionTypes.HealthCheck, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.ActionType, SpecialVariables.ActionTypes.Manual, StringComparison.OrdinalIgnoreCase));
    }

    private static bool StepReferencesPackages(DeploymentStepDto step, HashSet<string> packageActionNames)
    {
        return step.Actions.Any(a => packageActionNames.Contains(a.Name));
    }

    private static DeploymentStepDto BuildAcquirePackagesStep()
    {
        return new DeploymentStepDto
        {
            Id = -1,
            Name = "Acquire Packages",
            StepType = "AcquirePackages",
            Condition = "Success",
            StartTrigger = SpecialVariables.StartTriggers.StartAfterPrevious,
            PackageRequirement = string.Empty,
            IsRequired = true,
            Actions = new List<DeploymentActionDto>
            {
                new()
                {
                    Id = -1,
                    Name = "Acquire Packages",
                    ActionType = SpecialVariables.ActionTypes.TentaclePackage,
                    ActionOrder = 1,
                    IsRequired = true,
                    Properties = new List<DeploymentActionPropertyDto>()
                }
            }
        };
    }
}
