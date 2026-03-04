using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Core;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleFlavorCatalogTests
{
    [Fact]
    public void DiscoverBuiltInFlavors_Includes_KubernetesAgent()
    {
        var flavors = TentacleFlavorCatalog.DiscoverBuiltInFlavors();

        flavors.ShouldNotBeEmpty();
        flavors.Any(f => f.Id == "KubernetesAgent").ShouldBeTrue();
    }

    [Fact]
    public void DiscoverBuiltInFlavors_Has_Distinct_Ids_CaseInsensitive()
    {
        var flavors = TentacleFlavorCatalog.DiscoverBuiltInFlavors();
        var duplicates = flavors
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.ShouldBeEmpty();
    }
}
