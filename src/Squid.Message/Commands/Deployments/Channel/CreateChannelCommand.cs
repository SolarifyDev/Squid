using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Channel;

public class CreateChannelCommand : ICommand
{
    public CreateOrUpdateChannelModel Channel { get; set; }
}

public class CreateChannelResponse : SquidResponse<ChannelDto>
{
}