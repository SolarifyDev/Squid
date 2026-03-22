using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Machine;

public class KubernetesApiEndpointDto
{
    // Core
    public string CommunicationStyle { get; set; }
    public string ClusterUrl { get; set; }
    public string Namespace { get; set; }
    public string SkipTlsVerification { get; set; }
    public string ClusterCertificatePath { get; set; }

    // Provider-specific (type-discriminated)
    public KubernetesApiEndpointProviderType ProviderType { get; set; }
    public string ProviderConfig { get; set; }

    // Proxy (optional)
    public KubernetesApiEndpointProxyConfig Proxy { get; set; }

    // References
    public List<EndpointResourceReference> ResourceReferences { get; set; }
}
