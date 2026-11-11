using Squid.Core.Services.OctopusImport.Mapping.Scripts;
using Squid.Core.VariableSubstitution;
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
    [InlineData("#{Octopus.Release.Number | Replace \"Octopus.Release.Number\" \"x\"}", "#{Squid.Release.Number | Replace \"Octopus.Release.Number\" \"x\"}")]
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
    [InlineData("#{Url | Replace \"Octopus.Release.Number\" \"x\"}")]
    [InlineData("#{if Squid.Environment.Name == \"Octopus.Environment.Name\"}same#{/if}")]
    [InlineData("#{Url | Replace \"prefix Octopus.Release.Number suffix\" \"x\"}")]
    [InlineData("")]
    [InlineData(null)]
    public void RewriteReferences_UnmappedOrOutsideExpressions_AreLeftByteForByte(string input)
    {
        OctopusSystemVariables.RewriteReferences(input).ShouldBe(input);
    }

    [Theory]
    [InlineData("##{Octopus.Release.Number}", "##{Octopus.Release.Number}")]
    [InlineData("###{Octopus.Release.Number}", "###{Squid.Release.Number}")]
    [InlineData("a ##{Octopus.Project.Name} b #{Octopus.Project.Name}", "a ##{Octopus.Project.Name} b #{Squid.Project.Name}")]
    [InlineData("#{if Octopus.Environment.Name}##{Octopus.Release.Number}#{/if}", "#{if Squid.Environment.Name}##{Octopus.Release.Number}#{/if}")]
    [InlineData("#{unless Octopus.Release.Number}##{Octopus.Release.Number}#{/unless}", "#{unless Squid.Release.Number}##{Octopus.Release.Number}#{/unless}")]
    [InlineData("#{each m in Octopus.Machine.Roles}##{Octopus.Machine.Name}#{m}#{/each}", "#{each m in Squid.Machine.Roles}##{Octopus.Machine.Name}#{m}#{/each}")]
    [InlineData("#{A[#{Octopus.Environment.Name}]}", "#{A[#{Squid.Environment.Name}]}")]
    [InlineData("#{Octopus.Release.Number", "#{Octopus.Release.Number")]
    [InlineData("ok #{Octopus.Release.Number} but #{broken", "ok #{Octopus.Release.Number} but #{broken")]
    public void RewriteReferences_OnlyRewritesExpressionsTheTemplateEngineWouldEvaluate(string input, string expected)
    {
        OctopusSystemVariables.RewriteReferences(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("##{Octopus.Release.Number}")]
    [InlineData("###{Octopus.Release.Number}")]
    [InlineData("####{Octopus.Release.Number}")]
    [InlineData("#####{Octopus.Release.Number}")]
    [InlineData("x##{Octopus.Project.Name}y#{Octopus.Project.Name}z")]
    [InlineData("#{if Octopus.Environment.Name}##{Octopus.Release.Number}|#{Octopus.Release.Number}#{/if}")]
    [InlineData("#{unless Octopus.Environment.Name}hidden#{/unless}#{Octopus.Machine.Name | ToUpper}")]
    [InlineData("#{each m in Octopus.Machine.Roles}[#{m}]##{Octopus.Machine.Name}#{/each}")]
    [InlineData("#{if Octopus.Environment.Name == \"value-of-Octopus.Environment.Name\"}eq#{/if}")]
    [InlineData("$OctopusParameters['Octopus.Release.Number'] ## #{Octopus.Release.Number | ToUpper}")]
    [InlineData("#{Octopus.Release.Number")]
    public void RewriteReferences_NeverChangesWhatTheRealTemplateEngineEvaluatesTo(string input)
    {
        var dictionary = new VariableDictionary();

        foreach (var (octopusName, squidName) in OctopusSystemVariables.SquidEquivalents)
        {
            dictionary.Set(octopusName, $"value-of-{octopusName}");
            dictionary.Set(squidName, $"value-of-{octopusName}");
        }

        dictionary.Evaluate(OctopusSystemVariables.RewriteReferences(input)).ShouldBe(dictionary.Evaluate(input));
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
