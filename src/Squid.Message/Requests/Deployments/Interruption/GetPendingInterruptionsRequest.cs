using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Interruption;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Interruption;

[RequiresPermission(Permission.InterruptionView)]
public class GetPendingInterruptionsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ServerTaskId { get; set; }
}

public class GetPendingInterruptionsResponse : SquidResponse
{
    public List<InterruptionDto> Interruptions { get; set; } = new();
}
