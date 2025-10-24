using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process;

public class GetDeploymentProcessRequest : IRequest
{
    public int Id { get; set; }
}

public class GetDeploymentProcessResponse : SquidResponse<GetDeploymentProcessResponseData>
{
}

public class GetDeploymentProcessResponseData
{
    public DeploymentProcessDto DeploymentProcess { get; set; }
}
