using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process.Step;

public class GetDeploymentStepRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
    public int? ProcessId { get; set; }
    public int? ProjectId { get; set; }
    public int? RunbookId { get; set; }
}

public class GetDeploymentStepResponse : SquidResponse<DeploymentStepDto>
{
}
