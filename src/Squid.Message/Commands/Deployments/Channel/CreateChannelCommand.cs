using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Channel;

[RequiresPermission(Permission.ChannelCreate)]
public class CreateChannelCommand : ICommand
{
    public CreateOrUpdateChannelModel Channel { get; set; }
}

public class CreateChannelResponse : SquidResponse<ChannelDto>
{
}