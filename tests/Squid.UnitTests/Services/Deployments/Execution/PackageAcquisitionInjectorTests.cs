using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class PackageAcquisitionInjectorTests
{
    // === No packages → no injection ===

    [Fact]
    public void Inject_NoPackages_ReturnsOriginal()
    {
        var steps = new List<DeploymentStepDto> { BuildStep("Deploy", "Deploy App") };

        var result = PackageAcquisitionInjector.InjectAcquisitionSteps(steps, new List<ReleaseSelectedPackage>());

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("Deploy");
    }

    [Fact]
    public void Inject_NullPackages_ReturnsOriginal()
    {
        var steps = new List<DeploymentStepDto> { BuildStep("Deploy", "Deploy App") };

        var result = PackageAcquisitionInjector.InjectAcquisitionSteps(steps, null);

        result.Count.ShouldBe(1);
    }

    // === Post-acquisition with packages → inject ===

    [Fact]
    public void Inject_HasPostAcquisitionPackages_InjectsAcquireStep()
    {
        var steps = new List<DeploymentStepDto> { BuildStep("Deploy", "Deploy App") };
        var packages = new List<ReleaseSelectedPackage> { new() { ActionName = "Deploy App" } };

        var result = PackageAcquisitionInjector.InjectAcquisitionSteps(steps, packages);

        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("Acquire Packages");
        result[0].Actions[0].ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
        result[1].Name.ShouldBe("Deploy");
    }

    // === Pre-acquisition steps skip injection ===

    [Fact]
    public void Inject_AllPreAcquisition_NoInjection()
    {
        var steps = new List<DeploymentStepDto>
        {
            BuildStep("Pre-Check", "Pre-Check Action", packageRequirement: SpecialVariables.PackageRequirements.BeforePackageAcquisition)
        };
        var packages = new List<ReleaseSelectedPackage> { new() { ActionName = "Pre-Check Action" } };

        var result = PackageAcquisitionInjector.InjectAcquisitionSteps(steps, packages);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("Pre-Check");
    }

    // === HealthCheck then package → inject after health check ===

    [Fact]
    public void Inject_HealthCheckThenPackage_InjectsAfterHealthCheck()
    {
        var steps = new List<DeploymentStepDto>
        {
            BuildStep("Health Check", "HC Action", actionType: SpecialVariables.ActionTypes.HealthCheck),
            BuildStep("Deploy", "Deploy App")
        };
        var packages = new List<ReleaseSelectedPackage> { new() { ActionName = "Deploy App" } };

        var result = PackageAcquisitionInjector.InjectAcquisitionSteps(steps, packages);

        result.Count.ShouldBe(3);
        result[0].Name.ShouldBe("Health Check");
        result[1].Name.ShouldBe("Acquire Packages");
        result[2].Name.ShouldBe("Deploy");
    }

    // === Manual then package → inject after manual ===

    [Fact]
    public void Inject_ManualThenPackage_InjectsAfterManual()
    {
        var steps = new List<DeploymentStepDto>
        {
            BuildStep("Manual Approval", "Manual Action", actionType: SpecialVariables.ActionTypes.Manual),
            BuildStep("Deploy", "Deploy App")
        };
        var packages = new List<ReleaseSelectedPackage> { new() { ActionName = "Deploy App" } };

        var result = PackageAcquisitionInjector.InjectAcquisitionSteps(steps, packages);

        result.Count.ShouldBe(3);
        result[0].Name.ShouldBe("Manual Approval");
        result[1].Name.ShouldBe("Acquire Packages");
        result[2].Name.ShouldBe("Deploy");
    }

    // === No action matches packages → no injection ===

    [Fact]
    public void Inject_NoMatchingActions_NoInjection()
    {
        var steps = new List<DeploymentStepDto> { BuildStep("Deploy", "Deploy App") };
        var packages = new List<ReleaseSelectedPackage> { new() { ActionName = "Other Action" } };

        var result = PackageAcquisitionInjector.InjectAcquisitionSteps(steps, packages);

        result.Count.ShouldBe(1);
    }

    // === Helpers ===

    private static DeploymentStepDto BuildStep(string name, string actionName, string actionType = "Squid.KubernetesRunScript", string packageRequirement = "")
    {
        return new DeploymentStepDto
        {
            Name = name,
            PackageRequirement = packageRequirement,
            Condition = "Success",
            StartTrigger = SpecialVariables.StartTriggers.StartAfterPrevious,
            Actions = new List<DeploymentActionDto>
            {
                new() { Name = actionName, ActionType = actionType, Properties = new List<DeploymentActionPropertyDto>() }
            }
        };
    }
}
