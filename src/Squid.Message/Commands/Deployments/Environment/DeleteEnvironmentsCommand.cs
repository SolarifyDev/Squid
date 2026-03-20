using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Environment;

[RequiresPermission(Permission.EnvironmentDelete)]
public class DeleteEnvironmentsCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public List<int> Ids { get; set; }
}

public class DeleteEnvironmentsResponse : SquidResponse<DeleteEnvironmentsResponseData>
{
}

public class DeleteEnvironmentsResponseData
{
    public List<int> FailIds { get; set; }
}
