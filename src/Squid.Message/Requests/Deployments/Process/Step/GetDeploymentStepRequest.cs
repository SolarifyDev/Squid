using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process.Step;

public class GetDeploymentStepRequest : IRequest
{
    public int Id { get; set; }
}

public class GetDeploymentStepResponse : SquidResponse<DeploymentStepDto>
{
}
