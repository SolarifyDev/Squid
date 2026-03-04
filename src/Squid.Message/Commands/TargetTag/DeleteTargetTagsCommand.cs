using Squid.Message.Response;

namespace Squid.Message.Commands.TargetTag;

public class DeleteTargetTagsCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteTargetTagsResponse : SquidResponse<DeleteTargetTagsResponseData>
{
}

public class DeleteTargetTagsResponseData
{
    public List<int> FailIds { get; set; }
}
