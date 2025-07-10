using Squid.Message.Models.Deployments.Channel;

namespace Squid.Message.Events.Deployments.Channel;

public class ChannelUpdatedEvent : IEvent
{
    public ChannelDto Data { get; set; }
}