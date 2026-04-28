using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

/// <summary>
/// P0-Phase10.1 (audit C.3) — health check now reads RBAC dry-run results
/// from the agent's Capabilities metadata. Pre-fix the strategy only verified
/// Halibut polling worked; agents whose ServiceAccount RBAC was revoked or
/// whose namespace was deleted reported "healthy" until the first deploy
/// failed with a cryptic kubectl Forbidden error.
/// </summary>
public class KubernetesAgentHealthCheckStrategy : IHealthCheckStrategy
{
    /// <summary>
    /// Capabilities-metadata keys the agent populates by running
    /// <c>kubectl auth can-i &lt;verb&gt; &lt;resource&gt;</c> at startup.
    /// Server-side health check reads them; missing keys are tolerated for
    /// backward-compat with pre-Phase-10.1 agents that don't surface them.
    /// Pinned literals — operators reference these in alerting rules.
    /// </summary>
    public const string RbacCanCreatePodsKey = "kubernetes.canCreatePods";
    public const string RbacCanCreateConfigMapsKey = "kubernetes.canCreateConfigMaps";
    public const string RbacCanCreateSecretsKey = "kubernetes.canCreateSecrets";

    /// <summary>
    /// Resources whose CREATE permission is required for Squid's K8s deploy
    /// paths (RunScript, DeployYaml, DeployContainers, Helm). Loss of any one
    /// silently breaks at least one deploy flavor — strict-fail health check
    /// catches the regression BEFORE the deploy fails.
    /// </summary>
    private static readonly (string Key, string Resource)[] DeployCriticalPermissions =
    {
        (RbacCanCreatePodsKey, "pods"),
        (RbacCanCreateConfigMapsKey, "configmaps"),
        (RbacCanCreateSecretsKey, "secrets"),
    };

    private readonly IHalibutClientFactory _halibutClientFactory;

    public KubernetesAgentHealthCheckStrategy(IHalibutClientFactory halibutClientFactory)
    {
        _halibutClientFactory = halibutClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct, MachineHealthCheckPolicyDto healthCheckPolicy = null)
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

            // P0-Phase10.1: RBAC dry-run inspection. Agent populates these
            // metadata keys via `kubectl auth can-i create <resource>` at
            // startup; we fail fast if ANY deploy-critical permission is
            // missing. Older agents that don't surface these keys are
            // tolerated (no metadata → assume backward compat).
            var rbacFailure = InspectRbacMetadata(response.Metadata);
            if (rbacFailure != null)
                return new HealthCheckResult(false, rbacFailure);

            return new HealthCheckResult(true, $"Agent connected — version {response.AgentVersion}, services: {string.Join(", ", response.SupportedServices)}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Agent connectivity failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns a non-null actionable failure string when ANY deploy-critical
    /// RBAC permission is reported as "no" by the agent. Returns null when
    /// either (a) no metadata is present (old agent — tolerate), or (b) all
    /// reported permissions are "yes" (or unknown — agent didn't probe that
    /// specific resource).
    ///
    /// <para><c>internal</c> for direct unit testing.</para>
    /// </summary>
    internal static string InspectRbacMetadata(Dictionary<string, string> metadata)
    {
        if (metadata == null || metadata.Count == 0) return null;

        var deniedResources = new List<string>();

        foreach (var (key, resource) in DeployCriticalPermissions)
        {
            if (metadata.TryGetValue(key, out var value) &&
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                deniedResources.Add(resource);
            }
        }

        if (deniedResources.Count == 0) return null;

        var resourceList = string.Join(", ", deniedResources);
        return $"Agent RBAC dry-run failed — cannot create {resourceList}. " +
               $"Grant the agent's ServiceAccount RBAC permissions to create these resources " +
               $"in the target namespace, or check the namespace still exists.";
    }

    internal static ServiceEndPoint ParseAgentEndpoint(Machine machine)
    {
        return Machines.EndpointJsonHelper.ParseHalibutEndpoint(machine.Endpoint);
    }
}
