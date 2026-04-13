using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

public class TentacleHealthCheckStrategy : IHealthCheckStrategy
{
    private readonly IHalibutClientFactory _halibutClientFactory;

    public TentacleHealthCheckStrategy(IHalibutClientFactory halibutClientFactory)
    {
        _halibutClientFactory = halibutClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct, MachineHealthCheckPolicyDto healthCheckPolicy = null)
    {
        var endpoint = ParseEndpoint(machine);

        if (endpoint == null)
            return new HealthCheckResult(false, "Cannot construct Tentacle endpoint (missing Uri/SubscriptionId or Thumbprint)");

        try
        {
            var capsClient = _halibutClientFactory.CreateCapabilitiesClient(endpoint);
            var response = await capsClient.GetCapabilitiesAsync(new CapabilitiesRequest()).ConfigureAwait(false);

            if (response == null)
                return new HealthCheckResult(false, "Tentacle returned null capabilities response");

            return new HealthCheckResult(true, $"Tentacle connected — version {response.AgentVersion}, services: {string.Join(", ", response.SupportedServices)}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Tentacle connectivity failed: {ex.Message}");
        }
    }

    internal static global::Halibut.ServiceEndPoint ParseEndpoint(Machine machine)
    {
        var style = EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");

        return style == nameof(Message.Enums.CommunicationStyle.LinuxListening)
            ? EndpointJsonHelper.ParseTentacleListeningEndpoint(machine.Endpoint)
            : EndpointJsonHelper.ParseHalibutEndpoint(machine.Endpoint);
    }
}
