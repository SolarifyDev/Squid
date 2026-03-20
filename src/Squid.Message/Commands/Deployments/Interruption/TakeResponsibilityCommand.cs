using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Interruption;

[RequiresPermission(Permission.InterruptionSubmit)]
public class TakeResponsibilityCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int InterruptionId { get; set; }
    public string UserId { get; set; }
}

public class TakeResponsibilityResponse : SquidResponse
{
}
