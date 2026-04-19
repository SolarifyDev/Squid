using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// Enumerates every style-specific field on <see cref="UpdateMachineCommand"/>
/// with its "is set" predicate. Strategies use this to detect cross-style
/// contamination without each one re-enumerating the property list.
///
/// <para><b>Why centralised</b>: if a new style field is added to the
/// command, this is the ONE place to register it. Missing it here means
/// the strategy-uniqueness invariant can't catch cross-style contamination
/// for that field — which is exactly the R6-A class of bug we're fixing.
/// Unit tests on this inventory pin the "every new field is registered
/// here" contract.</para>
///
/// <para>Common fields (Name, IsDisabled, Roles, EnvironmentIds,
/// MachinePolicyId, MachineId, SpaceId) are intentionally excluded —
/// they're valid on every style and handled by the shared update path.</para>
/// </summary>
internal static class UpdateCommandFieldInventory
{
    /// <summary>
    /// Returns every style-specific field as a (name, isSet) tuple.
    /// Order is stable so tests can assert coverage deterministically.
    /// </summary>
    public static IReadOnlyList<StyleField> EnumerateStyleFields(UpdateMachineCommand c) => new StyleField[]
    {
        // KubernetesApi
        new(nameof(c.ClusterUrl), !string.IsNullOrEmpty(c.ClusterUrl)),
        new(nameof(c.SkipTlsVerification), c.SkipTlsVerification.HasValue),
        new(nameof(c.ProviderType), c.ProviderType.HasValue),
        new(nameof(c.ProviderConfig), !string.IsNullOrEmpty(c.ProviderConfig)),

        // KubernetesApi + KubernetesAgent (shared)
        new(nameof(c.Namespace), !string.IsNullOrEmpty(c.Namespace)),

        // KubernetesAgent
        new(nameof(c.ReleaseName), !string.IsNullOrEmpty(c.ReleaseName)),
        new(nameof(c.HelmNamespace), !string.IsNullOrEmpty(c.HelmNamespace)),
        new(nameof(c.ChartRef), !string.IsNullOrEmpty(c.ChartRef)),

        // TentaclePolling + KubernetesAgent (shared agent-identity)
        new(nameof(c.SubscriptionId), !string.IsNullOrEmpty(c.SubscriptionId)),

        // Tentacle* + KubernetesAgent (shared certificate identity)
        new(nameof(c.Thumbprint), !string.IsNullOrEmpty(c.Thumbprint)),

        // TentacleListening
        new(nameof(c.Uri), !string.IsNullOrEmpty(c.Uri)),
        new(nameof(c.ProxyId), c.ProxyId.HasValue),

        // OpenClaw
        new(nameof(c.BaseUrl), !string.IsNullOrEmpty(c.BaseUrl)),
        new(nameof(c.InlineGatewayToken), !string.IsNullOrEmpty(c.InlineGatewayToken)),
        new(nameof(c.InlineHooksToken), !string.IsNullOrEmpty(c.InlineHooksToken)),

        // SSH
        new(nameof(c.Host), !string.IsNullOrEmpty(c.Host)),
        new(nameof(c.Port), c.Port.HasValue),
        new(nameof(c.Fingerprint), !string.IsNullOrEmpty(c.Fingerprint)),
        new(nameof(c.RemoteWorkingDirectory), !string.IsNullOrEmpty(c.RemoteWorkingDirectory)),
        new(nameof(c.ProxyType), c.ProxyType.HasValue),
        new(nameof(c.ProxyHost), !string.IsNullOrEmpty(c.ProxyHost)),
        new(nameof(c.ProxyPort), c.ProxyPort.HasValue),
        new(nameof(c.ProxyUsername), !string.IsNullOrEmpty(c.ProxyUsername)),
        new(nameof(c.ProxyPassword), !string.IsNullOrEmpty(c.ProxyPassword)),

        // Shared endpoint-level (K8sApi / OpenClaw / SSH)
        new(nameof(c.ResourceReferences), c.ResourceReferences != null),
    };

    public readonly record struct StyleField(string Name, bool IsSet);
}
