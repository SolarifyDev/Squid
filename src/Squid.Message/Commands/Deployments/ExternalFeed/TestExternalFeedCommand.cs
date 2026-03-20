using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ExternalFeed;

[RequiresPermission(Permission.FeedView)]
public class TestExternalFeedCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
}

public class TestExternalFeedResponse : SquidResponse<TestExternalFeedResponseData>
{
}

public class TestExternalFeedResponseData
{
    public bool Success { get; set; }
    public string Message { get; set; }
}
