using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Support.Scenarios;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleScenarioMatrixTests
{
    [Fact]
    public void KubernetesAgentRuntimeSmoke_Has_Stable_Display_Names()
    {
        var cases = TentacleScenarioMatrix.KubernetesAgentRuntimeSmoke().ToList();

        cases.ShouldNotBeEmpty();
        cases.Select(c => c.Name).ShouldAllBe(n => n.Contains("KubernetesAgent"));
        cases.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count().ShouldBe(cases.Count);
    }

    [Fact]
    public void KubernetesAgentClusterScenarios_Are_Marked_As_ExternalDependency()
    {
        var scenarios = TentacleScenarioMatrix.KubernetesAgentClusterScenarios().ToList();

        scenarios.ShouldNotBeEmpty();
        scenarios.ShouldAllBe(s => s.RequiresKubernetesCluster && s.RequiresHelm);
    }
}
