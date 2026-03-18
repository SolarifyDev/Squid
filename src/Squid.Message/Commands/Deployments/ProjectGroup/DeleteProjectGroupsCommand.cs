using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ProjectGroup;

[RequiresPermission(Permission.ProjectDelete)]
public class DeleteProjectGroupsCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public List<int> Ids { get; set; }
}

public class DeleteProjectGroupsResponse : SquidResponse<DeleteProjectGroupsResponseData>
{
}

public class DeleteProjectGroupsResponseData
{
    public List<int> FailIds { get; set; }
}
