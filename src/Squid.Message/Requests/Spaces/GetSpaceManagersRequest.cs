using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Spaces;
using Squid.Message.Response;

namespace Squid.Message.Requests.Spaces;

[RequiresPermission(Permission.SpaceView)]
public class GetSpaceManagersRequest : IRequest
{
    public int SpaceId { get; set; }
}

public class GetSpaceManagersResponse : SquidResponse<List<SpaceManagerTeamDto>>
{
}
