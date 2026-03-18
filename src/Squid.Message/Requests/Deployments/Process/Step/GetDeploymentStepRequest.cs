using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process.Step;

[RequiresPermission(Permission.ProcessView)]
public class GetDeploymentStepRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
}

public class GetDeploymentStepResponse : SquidResponse<DeploymentStepDto>
{
}
