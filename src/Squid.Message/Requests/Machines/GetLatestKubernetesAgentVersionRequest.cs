using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

[RequiresPermission(Permission.MachineView)]
public class GetLatestKubernetesAgentVersionRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
}

public class GetLatestKubernetesAgentVersionResponse : SquidResponse<GetLatestKubernetesAgentVersionResponseData>
{
}

public class GetLatestKubernetesAgentVersionResponseData
{
    public string LatestVersion { get; set; }
}
