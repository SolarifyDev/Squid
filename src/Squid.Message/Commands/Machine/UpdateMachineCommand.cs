using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

/// <summary>
/// Update a machine's metadata and/or endpoint-specific fields.
///
/// <para><b>Style-specific fields are routed server-side to the correct
/// update strategy</b> (Round-6 R6-F): sending <c>ClusterUrl</c> to a
/// TentaclePolling machine, for instance, throws
/// <c>MachineEndpointUpdateNotApplicableException</c> (HTTP 400) rather
/// than silently corrupting the endpoint JSON — a bug that persisted
/// through 5 rounds of audit on the upgrade module because the update
/// path had not been systematically scanned. See
/// <c>docs/tentacle-self-upgrade-design.md</c> §6 R6 audit.</para>
///
/// <para>Common fields apply to every style: Name, IsDisabled, Roles,
/// EnvironmentIds, MachinePolicyId. Style-specific fields only apply
/// when the machine is of the matching style.</para>
/// </summary>
[RequiresPermission(Permission.MachineEdit)]
public class UpdateMachineCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }

    // ── Common (any style) ──────────────────────────────────────────────

    public string Name { get; set; }

    public bool? IsDisabled { get; set; }

    public List<string> Roles { get; set; }

    public List<int> EnvironmentIds { get; set; }

    public int? MachinePolicyId { get; set; }

    // ── KubernetesApi endpoint ──────────────────────────────────────────

    public string ClusterUrl { get; set; }

    /// <summary>Shared with KubernetesAgent.</summary>
    public string Namespace { get; set; }

    public bool? SkipTlsVerification { get; set; }

    public KubernetesApiEndpointProviderType? ProviderType { get; set; }

    public string ProviderConfig { get; set; }

    // ── KubernetesAgent endpoint ────────────────────────────────────────
    // (SubscriptionId + Thumbprint are shared with TentaclePolling; Namespace shared above)

    public string ReleaseName { get; set; }

    public string HelmNamespace { get; set; }

    public string ChartRef { get; set; }

    // ── TentaclePolling + KubernetesAgent shared agent-identity fields ──

    public string SubscriptionId { get; set; }

    /// <summary>Shared with TentacleListening + KubernetesAgent.</summary>
    public string Thumbprint { get; set; }

    // ── TentacleListening endpoint ──────────────────────────────────────
    // (Thumbprint shared above)

    public string Uri { get; set; }

    public int? ProxyId { get; set; }

    // ── OpenClaw endpoint ───────────────────────────────────────────────

    public string BaseUrl { get; set; }

    public string InlineGatewayToken { get; set; }

    public string InlineHooksToken { get; set; }

    // ── SSH endpoint ────────────────────────────────────────────────────

    public string Host { get; set; }

    public int? Port { get; set; }

    public string Fingerprint { get; set; }

    public string RemoteWorkingDirectory { get; set; }

    public SshProxyType? ProxyType { get; set; }

    public string ProxyHost { get; set; }

    public int? ProxyPort { get; set; }

    public string ProxyUsername { get; set; }

    public string ProxyPassword { get; set; }

    // ── Shared across endpoints that support it (K8sApi / OpenClaw / SSH) ──

    public List<EndpointResourceReference> ResourceReferences { get; set; }
}

public class UpdateMachineResponse : SquidResponse<MachineDto>
{
}
