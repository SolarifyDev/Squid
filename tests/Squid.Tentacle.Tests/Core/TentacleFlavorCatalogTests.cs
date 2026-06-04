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
    public void DiscoverBuiltInFlavors_Includes_Tentacle()
    {
        var flavors = TentacleFlavorCatalog.DiscoverBuiltInFlavors();

        flavors.Any(f => f.Id == "Tentacle").ShouldBeTrue(
            customMessage: "the renamed cross-platform 'Tentacle' flavor must be reflection-discovered.");
    }

    [Fact]
    public void RealStartupPath_ResolvesLegacyLinuxTentacleId_AfterUpgrade()
    {
        // The exact path TentacleApp uses at startup: discover built-in flavors, build the
        // resolver, resolve the persisted Tentacle:Flavor value. An agent registered BEFORE the
        // rename has Flavor=LinuxTentacle in its config; after upgrading to the renamed binary it
        // MUST still resolve (via the alias) to the same flavor as the new "Tentacle" id —
        // otherwise every existing agent would fail to start post-upgrade.
        var resolver = new TentacleFlavorResolver(TentacleFlavorCatalog.DiscoverBuiltInFlavors());

        var legacy = resolver.Resolve("LinuxTentacle");
        var current = resolver.Resolve("Tentacle");

        legacy.Id.ShouldBe("Tentacle");
        legacy.ShouldBeSameAs(current);
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
