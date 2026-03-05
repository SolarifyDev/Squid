using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

public class GetLatestKubernetesAgentVersionRequest : IRequest
{
}

public class GetLatestKubernetesAgentVersionResponse : SquidResponse<GetLatestKubernetesAgentVersionResponseData>
{
}

public class GetLatestKubernetesAgentVersionResponseData
{
    public string LatestVersion { get; set; }
}
