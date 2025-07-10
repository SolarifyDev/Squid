using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Channel;

public class DeleteChannelsCommand : ICommand
{
    public List<Guid> Ids { get; set; }
}

public class DeleteChannelsResponse : SquidResponse<DeleteChannelsResponseData>
{
}

public class DeleteChannelsResponseData
{
    public List<Guid> FailIds { get; set; }
}