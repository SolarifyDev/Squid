using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ExternalFeed;

[RequiresPermission(Permission.FeedEdit)]
public class DeleteExternalFeedsCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public List<int> Ids { get; set; }
}

public class DeleteExternalFeedsResponse : SquidResponse<DeleteExternalFeedsResponseData>
{
}

public class DeleteExternalFeedsResponseData
{
    public List<int> FailIds { get; set; }
}
