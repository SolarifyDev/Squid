using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ExternalFeed;

public class DeleteExternalFeedsCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteExternalFeedsResponse : SquidResponse<DeleteExternalFeedsResponseData>
{
}

public class DeleteExternalFeedsResponseData
{
    public List<int> FailIds { get; set; }
}
