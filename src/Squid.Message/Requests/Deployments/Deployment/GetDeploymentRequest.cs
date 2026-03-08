using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.ServerTask;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Deployment;

public class GetDeploymentRequest : IRequest
{
    public int Id { get; set; }

    public bool? Verbose { get; set; }

    public int? Tail { get; set; }
}

public class GetDeploymentResponse : SquidResponse<GetDeploymentResponseData>;

public class GetDeploymentResponseData
{
    public DeploymentDto Deployment { get; set; }

    public ServerTaskDetailsDto TaskDetails { get; set; }
}
