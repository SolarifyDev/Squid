using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

public class GetConnectionStatusRequest : IRequest
{
    public string SubscriptionId { get; set; }
}

public class GetConnectionStatusResponse : SquidResponse<GetConnectionStatusResponseData>
{
}

public class GetConnectionStatusResponseData
{
    public bool Connected { get; set; }
}
