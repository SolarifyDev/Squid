using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class UpdateMachineCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }

    public string Name { get; set; }

    public bool? IsDisabled { get; set; }

    public List<string> Roles { get; set; }

    public List<int> EnvironmentIds { get; set; }

    public int? MachinePolicyId { get; set; }

    // Endpoint — Kubernetes (optional — only processed when at least one is set)
    public string ClusterUrl { get; set; }
    public string Namespace { get; set; }
    public bool? SkipTlsVerification { get; set; }
    public KubernetesApiEndpointProviderType? ProviderType { get; set; }
    public string ProviderConfig { get; set; }
    public List<EndpointResourceReference> ResourceReferences { get; set; }

    // Endpoint — OpenClaw (optional — only processed when at least one is set)
    public string BaseUrl { get; set; }
    public string InlineGatewayToken { get; set; }
    public string InlineHooksToken { get; set; }
    public string WebSocketUrl { get; set; }
}

public class UpdateMachineResponse : SquidResponse<MachineDto>
{
}
