using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Scenarios;

namespace Squid.Tentacle.Tests.Core;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleFlavorScenarioCoverageContractTests
{
    [Fact]
    public void RuntimeSmokeScenarios_Reference_Only_Discoverable_Flavors()
    {
        var flavorIds = TentacleFlavorCatalog.DiscoverBuiltInFlavors()
            .Select(f => f.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var runtimeSmokeScenarios = TentacleScenarioMatrix.KubernetesAgentRuntimeSmoke().ToList();
        runtimeSmokeScenarios.ShouldNotBeEmpty();

        runtimeSmokeScenarios.ShouldAllBe(s => flavorIds.Contains(s.FlavorId));
    }

    [Fact]
    public void Every_BuiltIn_Flavor_Has_At_Least_One_RuntimeSmoke_Scenario()
    {
        var flavorIds = TentacleFlavorCatalog.DiscoverBuiltInFlavors()
            .Select(f => f.Id)
            .ToList();

        var runtimeSmokeByFlavor = TentacleScenarioMatrix.KubernetesAgentRuntimeSmoke()
            .GroupBy(s => s.FlavorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var flavorId in flavorIds)
        {
            runtimeSmokeByFlavor.ContainsKey(flavorId)
                .ShouldBeTrue($"Built-in flavor '{flavorId}' should have at least one runtime smoke scenario in TentacleScenarioMatrix.");
        }
    }
}
