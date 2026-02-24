using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Tests.Support.Scenarios;

public enum TentacleExecutionBackendKind
{
    LocalProcess,
    KubernetesScriptPod
}

public enum TentacleCommunicationKind
{
    HalibutPolling
}

public sealed record TentacleScenarioCase(
    string Name,
    string FlavorId,
    TentacleExecutionBackendKind ExecutionBackend,
    TentacleCommunicationKind Communication,
    bool RequiresKubernetesCluster = false,
    bool RequiresHelm = false)
{
    public TentacleFlavorContext CreateContext()
    {
        var tentacleSettings = new TentacleSettings
        {
            Flavor = FlavorId
        };

        var kubernetesSettings = new KubernetesSettings
        {
            UseScriptPods = ExecutionBackend == TentacleExecutionBackendKind.KubernetesScriptPod
        };

        return new TentacleFlavorContext
        {
            TentacleSettings = tentacleSettings,
            KubernetesSettings = kubernetesSettings
        };
    }

    public override string ToString() => Name;
}
