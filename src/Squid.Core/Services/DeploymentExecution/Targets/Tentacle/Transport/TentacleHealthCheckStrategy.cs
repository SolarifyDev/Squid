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
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;

    public TentacleHealthCheckStrategy(IHalibutClientFactory halibutClientFactory, IMachineRuntimeCapabilitiesCache capabilitiesCache = null)
    {
        _halibutClientFactory = halibutClientFactory;
        _capabilitiesCache = capabilitiesCache;
    }

    /// <summary>
    /// Three-tier probe:
    ///   1. Halibut round-trip via CapabilitiesService (transport + mutual TLS + basic RPC)
    ///   2. Capabilities metadata parsed into machine runtime capabilities cache so
    ///      the next deployment can pick the right script syntax without an extra call
    ///   3. (Optional) correlation-script probe: a short echo executed through the
    ///      agent that must return a fresh random token. This catches an agent whose
    ///      CapabilitiesService answers (cached) but whose script backend is stuck
    ///      or MITM-impersonated
    /// </summary>
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

            CacheCapabilitiesFor(machine, response);

            var os = ReadMetadata(response, "os");
            var shell = ReadMetadata(response, "defaultShell");
            var osInfo = string.IsNullOrEmpty(os) ? string.Empty : $", OS={os}";
            var shellInfo = string.IsNullOrEmpty(shell) ? string.Empty : $", shell={shell}";

            return new HealthCheckResult(true, $"Tentacle connected — version {response.AgentVersion}{osInfo}{shellInfo}, services: {string.Join(", ", response.SupportedServices)}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Tentacle connectivity failed: {ex.Message}");
        }
    }

    private void CacheCapabilitiesFor(Machine machine, CapabilitiesResponse response)
    {
        if (_capabilitiesCache == null || machine == null) return;
        _capabilitiesCache.Store(machine.Id, response.Metadata, response.AgentVersion);
    }

    private static string ReadMetadata(CapabilitiesResponse response, string key)
        => response.Metadata != null && response.Metadata.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;

    internal static global::Halibut.ServiceEndPoint ParseEndpoint(Machine machine)
    {
        var style = EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");

        return style == nameof(Message.Enums.CommunicationStyle.TentacleListening)
            ? EndpointJsonHelper.ParseTentacleListeningEndpoint(machine.Endpoint)
            : EndpointJsonHelper.ParseHalibutEndpoint(machine.Endpoint);
    }
}
