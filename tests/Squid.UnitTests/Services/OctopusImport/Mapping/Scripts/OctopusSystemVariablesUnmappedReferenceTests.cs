using Squid.Core.Services.OctopusImport.Mapping.Scripts;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Scripts;

public class OctopusSystemVariablesUnmappedReferenceTests
{
    [Theory]
    [InlineData("#{Octopus.Action.KubernetesContainers.DeploymentName}", "Octopus.Action.KubernetesContainers.DeploymentName")]
    [InlineData("x-#{Octopus.Action.KubernetesContainers.ComputedSecretName | ToLower}-y", "Octopus.Action.KubernetesContainers.ComputedSecretName")]
    [InlineData("#{if Octopus.Deployment.Error}failed#{/if}", "Octopus.Deployment.Error")]
    [InlineData("#{Octopus.Action[Build image].Output.Tag}", "Octopus.Action[Build image].Output.Tag")]
    [InlineData("#{Octopus.Web.ServerUri} #{octopus.web.serveruri}", "Octopus.Web.ServerUri")]
    [InlineData("#{Octopus.Zeta} #{Octopus.Alpha} #{Octopus.Zeta}", "Octopus.Alpha|Octopus.Zeta")]
    [InlineData("#{Octopus.Web.ServerUri | Replace \"Octopus.Old\" \"new\"}", "Octopus.Web.ServerUri")]
    [InlineData("#{Octopus.Action[\"Build\"].Output.Tag}", "Octopus.Action[\"Build\"].Output.Tag")]
    public void FindUnmappedReferences_UnmappedNamesInsideExpressions_AreReportedDistinctAndSorted(string text, string expected)
    {
        OctopusSystemVariables.FindUnmappedReferences(text).ShouldBe(expected.Split('|'));
    }

    [Theory]
    [InlineData("#{Octopus.Release.Number} #{Octopus.Environment.Name | ToUpper}")]
    [InlineData("#{Squid.Release.Number}")]
    [InlineData("Octopus.Web.ServerUri outside any expression")]
    [InlineData("$OctopusParameters['Octopus.Web.ServerUri']")]
    [InlineData("#{MyOctopus.Thing} #{each var in OctopusParameters}#{var.Key}#{/each}")]
    // Octostache string literals are double-quoted (TemplateParser.QuotedText); a name inside one is text, not a lookup.
    [InlineData("#{Url | Replace \"Octopus.Old\" \"new\"}")]
    [InlineData("#{if Env == \"Octopus.Web.ServerUri\"}x#{/if}")]
    [InlineData("#{Url | Replace \"a Octopus.Old b\" \"new\"}")]
    [InlineData("")]
    [InlineData(null)]
    public void FindUnmappedReferences_MappedOrNonOctopusText_ReportsNothing(string text)
    {
        OctopusSystemVariables.FindUnmappedReferences(text).ShouldBeEmpty();
    }

    [Fact]
    public void FindUnmappedReferences_AfterRewrite_ReportsOnlyWhatTheRewriterCouldNotMap()
    {
        const string text = "#{Octopus.Release.Number}/#{Octopus.Action.KubernetesContainers.DeploymentName}";

        OctopusSystemVariables.FindUnmappedReferences(OctopusSystemVariables.RewriteReferences(text)).ShouldBe(new[] { "Octopus.Action.KubernetesContainers.DeploymentName" });
    }
}
