using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ExternalFeed;

public class DeleteExternalFeedsCommand : ICommand
{
    public List<Guid> Ids { get; set; }
}

public class DeleteExternalFeedsResponse : SquidResponse<DeleteExternalFeedsResponseData>
{
}

public class DeleteExternalFeedsResponseData
{
    public List<Guid> FailIds { get; set; }
} 