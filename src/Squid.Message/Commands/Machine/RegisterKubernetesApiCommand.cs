using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class RegisterKubernetesApiCommand : ICommand, ISpaceScoped
{
    public string MachineName { get; set; }
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public List<string> Roles { get; set; }
    public List<int> EnvironmentIds { get; set; }

    // Endpoint
    public string ClusterUrl { get; set; }
    public string Namespace { get; set; } = "default";
    public bool SkipTlsVerification { get; set; }

    // Resources — at least one required
    public List<EndpointResourceReference> ResourceReferences { get; set; }
}
