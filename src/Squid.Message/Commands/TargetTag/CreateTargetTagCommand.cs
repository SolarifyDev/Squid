using Squid.Message.Models.Deployments.TargetTag;
using Squid.Message.Response;

namespace Squid.Message.Commands.TargetTag;

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
