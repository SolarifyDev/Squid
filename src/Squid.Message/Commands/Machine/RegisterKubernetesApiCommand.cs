using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

public class RegisterKubernetesApiCommand : ICommand
{
    public string MachineName { get; set; }
    public int SpaceId { get; set; }
    public List<string> Roles { get; set; }
    public List<int> EnvironmentIds { get; set; }

    // Endpoint
    public string ClusterUrl { get; set; }
    public string Namespace { get; set; } = "default";
    public bool SkipTlsVerification { get; set; }

    // Resources — at least one required
    public List<EndpointResourceReference> ResourceReferences { get; set; }
}
