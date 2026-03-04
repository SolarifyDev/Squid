using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Scenarios;

namespace Squid.Tentacle.Tests.Core;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleFlavorRuntimeContractTests
{
    [Theory]
    [TentacleScenarioData(TentacleScenarioSet.KubernetesAgentRuntimeSmoke)]
    public void RuntimeSmokeScenario_Creates_Valid_Runtime_Contract(TentacleScenarioCase scenario)
    {
        var flavor = new TentacleFlavorResolver(TentacleFlavorCatalog.DiscoverBuiltInFlavors())
            .Resolve(scenario.FlavorId);

        var runtime = flavor.CreateRuntime(scenario.CreateContext());

        runtime.Registrar.ShouldNotBeNull();
        runtime.ScriptBackend.ShouldNotBeNull();
        runtime.StartupHooks.ShouldNotBeNull();
        runtime.BackgroundTasks.ShouldNotBeNull();

        runtime.StartupHooks.Select(h => h.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(runtime.StartupHooks.Count, "Startup hook names should be non-empty and unique per flavor runtime.");

        runtime.BackgroundTasks.Select(t => t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(runtime.BackgroundTasks.Count, "Background task names should be non-empty and unique per flavor runtime.");
    }
}
