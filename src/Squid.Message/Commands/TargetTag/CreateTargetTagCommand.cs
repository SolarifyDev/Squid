using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.TargetTag;
using Squid.Message.Response;

namespace Squid.Message.Commands.TargetTag;

[RequiresPermission(Permission.MachineEdit)]
public class CreateTargetTagCommand : ICommand
{
    public string Name { get; set; }

    public int SpaceId { get; set; }
}

public class CreateTargetTagResponse : SquidResponse<CreateTargetTagResponseData>
{
}

public class CreateTargetTagResponseData
{
    public TargetTagDto TargetTag { get; set; }
}
