using Squid.Message.Models.Deployments.Interruption;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Interruption;

public class GetPendingInterruptionsRequest : IRequest
{
    public int ServerTaskId { get; set; }
}

public class GetPendingInterruptionsResponse : SquidResponse
{
    public List<InterruptionDto> Interruptions { get; set; } = new();
}
