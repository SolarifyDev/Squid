using Squid.Tentacle.Flavors.KubernetesAgent;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Scenarios;

namespace Squid.Tentacle.Tests.Flavors.KubernetesAgent;

[Trait("Category", TentacleTestCategories.Flavor)]
public class KubernetesAgentFlavorRuntimeTests
{
    [Theory]
    [TentacleScenarioData(TentacleScenarioSet.KubernetesAgentRuntimeSmoke)]
    public void CreateRuntime_LocalProcessScenario_Wires_KubernetesAgentRuntime(TentacleScenarioCase scenario)
    {
        var flavor = new KubernetesAgentFlavor();

        var runtime = flavor.CreateRuntime(scenario.CreateContext());

        flavor.Id.ShouldBe("KubernetesAgent");
        runtime.Registrar.ShouldBeOfType<KubernetesAgentRegistrar>();
        runtime.ScriptBackend.ShouldBeOfType<LocalScriptService>();
        runtime.BackgroundTasks.ShouldBeEmpty();
        runtime.StartupHooks.ShouldNotBeEmpty();
        runtime.StartupHooks.Select(h => h.Name).ShouldContain("InitializationFlag");
    }
}
