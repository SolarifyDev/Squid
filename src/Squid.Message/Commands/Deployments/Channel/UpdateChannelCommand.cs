using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Channel;

[RequiresPermission(Permission.ChannelEdit)]
public class UpdateChannelCommand : ICommand
{
    public CreateOrUpdateChannelModel Channel { get; set; }
}

public class UpdateChannelResponse : SquidResponse<ChannelDto>
{
}