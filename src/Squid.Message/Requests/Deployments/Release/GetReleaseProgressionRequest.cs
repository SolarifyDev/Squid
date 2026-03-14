using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

public class GetReleaseProgressionRequest : IRequest
{
    public int ReleaseId { get; set; }
}

public class GetReleaseProgressionResponse : SquidResponse<ReleaseLifecycleProgressionDto>
{
}
