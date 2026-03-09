using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentHealthCheckStrategy : IHealthCheckStrategy
{
    private readonly IHalibutClientFactory _halibutClientFactory;

    public KubernetesAgentHealthCheckStrategy(IHalibutClientFactory halibutClientFactory)
    {
        _halibutClientFactory = halibutClientFactory;
    }

    public string DefaultHealthCheckScript => """
                                              #!/bin/bash
                                              echo "Health check started (KubernetesAgent)"
                                              echo "Hostname: $(hostname)"
                                              echo "Date: $(date -u)"
                                              kubectl get pods --namespace=${NAMESPACE:-default} -o name 2>&1 | head -5
                                              echo "Health check completed"
                                              exit 0
                                              """;

    public async Task<HealthCheckResult> CheckConnectivityAsync(Machine machine, CancellationToken ct)
    {
        var endpoint = ParseAgentEndpoint(machine);

        if (endpoint == null)
            return new HealthCheckResult(false, "Cannot construct agent polling endpoint (missing SubscriptionId or Thumbprint)");

        try
        {
            var capsClient = _halibutClientFactory.CreateCapabilitiesClient(endpoint);
            var response = await capsClient.GetCapabilitiesAsync(new CapabilitiesRequest()).ConfigureAwait(false);

            if (response == null)
                return new HealthCheckResult(false, "Agent returned null capabilities response");

            return new HealthCheckResult(true, $"Agent connected — version {response.AgentVersion}, services: {string.Join(", ", response.SupportedServices)}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Agent connectivity failed: {ex.Message}");
        }
    }

    internal static ServiceEndPoint ParseAgentEndpoint(Machine machine)
    {
        var uri = machine.Uri;

        if (string.IsNullOrEmpty(uri) && !string.IsNullOrEmpty(machine.PollingSubscriptionId))
            uri = $"poll://{machine.PollingSubscriptionId}/";

        if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(machine.Thumbprint))
            return null;

        return new ServiceEndPoint(uri, machine.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
    }
}
