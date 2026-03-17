using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Release;

[RequiresPermission(Permission.ReleaseDelete)]
public class DeleteReleaseCommand : ICommand
{
    public int ReleaseId { get; set; }
}

public class DeleteReleaseResponse : SquidResponse
{
}
