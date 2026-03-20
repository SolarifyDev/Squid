using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Channel;

[RequiresPermission(Permission.ChannelCreate)]
public class CreateChannelCommand : ICommand, ISpaceScoped
{
    public CreateOrUpdateChannelModel Channel { get; set; }
    int? ISpaceScoped.SpaceId => Channel?.SpaceId;
}

public class CreateChannelResponse : SquidResponse<ChannelDto>
{
}