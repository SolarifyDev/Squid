using Squid.Message.Commands.Deployments.Channel;

namespace Squid.Message.Events.Deployments.Channel;

public class ChannelDeletedEvent : IEvent
{
    public DeleteChannelsResponseData Data { get; set; }
}