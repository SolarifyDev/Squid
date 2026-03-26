using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentHealthCheckStrategy : IHealthCheckStrategy
{
    private readonly IHalibutClientFactory _halibutClientFactory;

    public KubernetesAgentHealthCheckStrategy(IHalibutClientFactory halibutClientFactory)
    {
        _halibutClientFactory = halibutClientFactory;
    }

    public ScriptSyntax ScriptSyntax => ScriptSyntax.Bash;

    public string DefaultHealthCheckScript => """
                                              #!/bin/bash
                                              set -e
                                              echo "Health check started (KubernetesAgent)"
                                              echo "Hostname: $(hostname)"
                                              echo "Date: $(date -u)"
                                              kubectl version 2>&1
                                              kubectl get pods --namespace=${NAMESPACE:-default} -o name 2>&1 | head -5
                                              echo "Health check completed"
                                              """;

    public async Task<HealthCheckResult> CheckConnectivityAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct)
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
        return Machines.EndpointJsonHelper.ParseHalibutEndpoint(machine.Endpoint);
    }
}
