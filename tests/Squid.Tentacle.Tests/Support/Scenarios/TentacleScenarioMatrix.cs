namespace Squid.Tentacle.Tests.Support.Scenarios;

public static class TentacleScenarioMatrix
{
    // Fast local coverage for flavor/runtime wiring. No external cluster dependencies.
    public static IEnumerable<TentacleScenarioCase> KubernetesAgentRuntimeSmoke()
    {
        yield return new TentacleScenarioCase(
            Name: "KubernetesAgent.LocalProcess.HalibutPolling",
            FlavorId: "KubernetesAgent",
            ExecutionBackend: TentacleExecutionBackendKind.LocalProcess,
            Communication: TentacleCommunicationKind.HalibutPolling);

        yield return new TentacleScenarioCase(
            Name: "Tentacle.LocalProcess.HalibutPolling",
            FlavorId: "Tentacle",
            ExecutionBackend: TentacleExecutionBackendKind.LocalProcess,
            Communication: TentacleCommunicationKind.HalibutPolling);

        yield return new TentacleScenarioCase(
            Name: "Tentacle.LocalProcess.HalibutListening",
            FlavorId: "Tentacle",
            ExecutionBackend: TentacleExecutionBackendKind.LocalProcess,
            Communication: TentacleCommunicationKind.HalibutListening);
    }

    // Future E2E/cluster-backed scenarios. Kept here so new suites reuse the same case names.
    public static IEnumerable<TentacleScenarioCase> KubernetesAgentClusterScenarios()
    {
        yield return new TentacleScenarioCase(
            Name: "KubernetesAgent.ScriptPod.HalibutPolling",
            FlavorId: "KubernetesAgent",
            ExecutionBackend: TentacleExecutionBackendKind.KubernetesScriptPod,
            Communication: TentacleCommunicationKind.HalibutPolling,
            RequiresKubernetesCluster: true,
            RequiresHelm: true);
    }
}
