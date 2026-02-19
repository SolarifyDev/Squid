using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

public class ReleaseSelectedPackageTests
{
    [Fact]
    public void BuildActionVariables_WithSelectedPackage_InjectsPackageVersion()
    {
        var baseVariables = new List<VariableDto>
        {
            new() { Name = "Existing", Value = "value" }
        };

        var selectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { ActionName = "Deploy", Version = "1.2.3" }
        };

        var action = new DeploymentActionDto { Name = "Deploy" };

        var result = BuildActionVariables(baseVariables, action, selectedPackages);

        result.Count.ShouldBe(2);
        result.ShouldContain(v => v.Name == SpecialVariables.Action.PackageVersion && v.Value == "1.2.3");
    }

    [Fact]
    public void BuildActionVariables_NoSelectedPackage_ReturnsOriginalVariables()
    {
        var baseVariables = new List<VariableDto>
        {
            new() { Name = "Existing", Value = "value" }
        };

        var selectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { ActionName = "OtherAction", Version = "1.0.0" }
        };

        var action = new DeploymentActionDto { Name = "Deploy" };

        var result = BuildActionVariables(baseVariables, action, selectedPackages);

        result.ShouldBeSameAs(baseVariables);
    }

    [Fact]
    public void BuildActionVariables_CaseInsensitiveActionNameMatch()
    {
        var baseVariables = new List<VariableDto>();

        var selectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { ActionName = "deploy", Version = "2.0.0" }
        };

        var action = new DeploymentActionDto { Name = "Deploy" };

        var result = BuildActionVariables(baseVariables, action, selectedPackages);

        result.ShouldContain(v => v.Name == SpecialVariables.Action.PackageVersion && v.Value == "2.0.0");
    }

    [Fact]
    public void BuildActionVariables_EmptySelectedPackages_ReturnsOriginalVariables()
    {
        var baseVariables = new List<VariableDto>
        {
            new() { Name = "Existing", Value = "value" }
        };

        var action = new DeploymentActionDto { Name = "Deploy" };

        var result = BuildActionVariables(baseVariables, action, new List<ReleaseSelectedPackage>());

        result.ShouldBeSameAs(baseVariables);
    }

    /// <summary>
    /// Extracts the BuildActionVariables logic for testability (mirrors DeploymentTaskExecutor.BuildActionVariables).
    /// </summary>
    private static List<VariableDto> BuildActionVariables(
        List<VariableDto> effectiveVariables,
        DeploymentActionDto action,
        List<ReleaseSelectedPackage> selectedPackages)
    {
        var selectedPackage = selectedPackages
            .FirstOrDefault(sp => string.Equals(sp.ActionName, action.Name, System.StringComparison.OrdinalIgnoreCase));

        if (selectedPackage == null) return effectiveVariables;

        var variables = new List<VariableDto>(effectiveVariables);

        variables.Add(new VariableDto
        {
            Name = SpecialVariables.Action.PackageVersion,
            Value = selectedPackage.Version
        });

        return variables;
    }
}
