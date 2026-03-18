using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process;

[RequiresPermission(Permission.ProcessEdit)]
public class DeleteDeploymentProcessCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
}

public class DeleteDeploymentProcessResponse : SquidResponse<DeleteDeploymentProcessResponseData>
{
}

public class DeleteDeploymentProcessResponseData
{
    public string Message { get; set; }
}
