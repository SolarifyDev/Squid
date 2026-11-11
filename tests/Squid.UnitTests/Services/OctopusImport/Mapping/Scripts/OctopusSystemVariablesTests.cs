using Squid.Core.Services.OctopusImport.Mapping.Scripts;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Scripts;

public class OctopusSystemVariablesTests
{
    [Theory]
    [InlineData("#{Octopus.Release.Number}", "#{Squid.Release.Number}")]
    [InlineData("v#{Octopus.Release.Number | ToLower}-x", "v#{Squid.Release.Number | ToLower}-x")]
    [InlineData("#{if Octopus.Environment.Name == \"PRD\"}prod#{/if}", "#{if Squid.Environment.Name == \"PRD\"}prod#{/if}")]
    [InlineData("#{octopus.project.name}", "#{Squid.Project.Name}")]
    [InlineData("#{Octopus.Deployment.CreatedBy.DisplayName}", "#{Squid.Deployment.CreatedBy.DisplayName}")]
    [InlineData("#{Octopus.Action.Package.PackageId}", "#{Squid.Action.Package.PackageId}")]
    public void RewriteReferences_MappedBuiltInsInsideExpressions_AreRewritten(string input, string expected)
    {
        OctopusSystemVariables.RewriteReferences(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Octopus.Release.Number")]
    [InlineData("#{Octopus.Release.Id}")]
    [InlineData("#{Octopus.Action[Build].Output.Tag}")]
    [InlineData("#{each var in OctopusParameters}#{var.Key}#{/each}")]
    [InlineData("$OctopusParameters[\"Octopus.Release.Number\"]")]
    [InlineData("#{MyOctopus.Release.Number}")]
    [InlineData("")]
    [InlineData(null)]
    public void RewriteReferences_UnmappedOrOutsideExpressions_AreLeftByteForByte(string input)
    {
        OctopusSystemVariables.RewriteReferences(input).ShouldBe(input);
    }

    [Fact]
    public void SquidEquivalents_PointAtRealSquidSystemVariables()
    {
        OctopusSystemVariables.SquidEquivalents["Octopus.Release.Number"].ShouldBe(SpecialVariables.Release.Number);
        OctopusSystemVariables.SquidEquivalents["Octopus.Deployment.CreatedBy.DisplayName"].ShouldBe("Squid.Deployment.CreatedBy.DisplayName");
        OctopusSystemVariables.SquidEquivalents.Values.ShouldAllBe(name => name.StartsWith("Squid."));
    }

    [Fact]
    public void DeploymentCreatedByVariableNames_ArePinned()
    {
        // Scripts imported from Octopus reference these names; renaming them silently breaks those scripts.
        SpecialVariables.Deployment.CreatedById.ShouldBe("Squid.Deployment.CreatedBy.Id");
        SpecialVariables.Deployment.CreatedByUsername.ShouldBe("Squid.Deployment.CreatedBy.Username");
        SpecialVariables.Deployment.CreatedByDisplayName.ShouldBe("Squid.Deployment.CreatedBy.DisplayName");
    }
}
