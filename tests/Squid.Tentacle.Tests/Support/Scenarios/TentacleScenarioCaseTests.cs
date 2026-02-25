using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Support.Scenarios;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleScenarioCaseTests
{
    [Fact]
    public void CreateContext_LocalProcess_Disables_ScriptPods()
    {
        var scenario = new TentacleScenarioCase(
            Name: "KubernetesAgent.LocalProcess.HalibutPolling",
            FlavorId: "KubernetesAgent",
            ExecutionBackend: TentacleExecutionBackendKind.LocalProcess,
            Communication: TentacleCommunicationKind.HalibutPolling);

        var context = scenario.CreateContext();

        context.TentacleSettings.Flavor.ShouldBe("KubernetesAgent");

        var k8sSettings = new KubernetesSettings();
        context.Configuration.GetSection("Kubernetes").Bind(k8sSettings);
        k8sSettings.UseScriptPods.ShouldBeFalse();
    }

    [Fact]
    public void CreateContext_KubernetesScriptPod_Enables_ScriptPods()
    {
        var scenario = new TentacleScenarioCase(
            Name: "KubernetesAgent.ScriptPod.HalibutPolling",
            FlavorId: "KubernetesAgent",
            ExecutionBackend: TentacleExecutionBackendKind.KubernetesScriptPod,
            Communication: TentacleCommunicationKind.HalibutPolling);

        var context = scenario.CreateContext();

        var k8sSettings = new KubernetesSettings();
        context.Configuration.GetSection("Kubernetes").Bind(k8sSettings);
        k8sSettings.UseScriptPods.ShouldBeTrue();
    }
}
