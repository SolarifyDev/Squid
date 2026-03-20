using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Spaces;
using Squid.Message.Response;

namespace Squid.Message.Requests.Spaces;

[RequiresPermission(Permission.SpaceView)]
public class GetSpaceRequest : IRequest
{
    public int Id { get; set; }
}

public class GetSpaceResponse : SquidResponse<SpaceDto>
{
}
