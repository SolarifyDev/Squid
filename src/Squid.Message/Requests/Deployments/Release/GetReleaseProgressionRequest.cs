using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

[RequiresPermission(Permission.ReleaseView)]
public class GetReleaseProgressionRequest : IRequest
{
    public int ReleaseId { get; set; }
}

public class GetReleaseProgressionResponse : SquidResponse<ReleaseLifecycleProgressionDto>
{
}
