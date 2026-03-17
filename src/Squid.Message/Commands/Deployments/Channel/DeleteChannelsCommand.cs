using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Channel;

[RequiresPermission(Permission.ChannelDelete)]
public class DeleteChannelsCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteChannelsResponse : SquidResponse<DeleteChannelsResponseData>
{
}

public class DeleteChannelsResponseData
{
    public List<int> FailIds { get; set; }
}
