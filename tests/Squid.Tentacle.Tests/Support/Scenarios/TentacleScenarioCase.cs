using Microsoft.Extensions.Configuration;
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

        var useScriptPods = ExecutionBackend == TentacleExecutionBackendKind.KubernetesScriptPod;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Kubernetes:UseScriptPods"] = useScriptPods.ToString()
            })
            .Build();

        return new TentacleFlavorContext
        {
            TentacleSettings = tentacleSettings,
            Configuration = configuration
        };
    }

    public override string ToString() => Name;
}
