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
    HalibutPolling,
    HalibutListening
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

        if (Communication == TentacleCommunicationKind.HalibutPolling && FlavorId == "LinuxTentacle")
            tentacleSettings.ServerCommsUrl = "https://localhost:10943";

        if (Communication == TentacleCommunicationKind.HalibutListening && FlavorId == "LinuxTentacle")
            tentacleSettings.ServerCertificate = "E2E_PLACEHOLDER_THUMBPRINT";

        var useScriptPods = ExecutionBackend == TentacleExecutionBackendKind.KubernetesScriptPod;

        var configData = new Dictionary<string, string>
        {
            ["Kubernetes:UseScriptPods"] = useScriptPods.ToString(),
            ["LinuxTentacle:WorkspacePath"] = "/opt/squid/work",
            ["LinuxTentacle:ListeningPort"] = "10933"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        return new TentacleFlavorContext
        {
            TentacleSettings = tentacleSettings,
            Configuration = configuration
        };
    }

    public override string ToString() => Name;
}
