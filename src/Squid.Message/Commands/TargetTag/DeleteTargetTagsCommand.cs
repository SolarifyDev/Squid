using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.TargetTag;

[RequiresPermission(Permission.MachineEdit)]
public class DeleteTargetTagsCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public List<int> Ids { get; set; }
}

public class DeleteTargetTagsResponse : SquidResponse<DeleteTargetTagsResponseData>
{
}

public class DeleteTargetTagsResponseData
{
    public List<int> FailIds { get; set; }
}
