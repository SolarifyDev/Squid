using Squid.Message.Commands.Deployments.Channel;

namespace Squid.Message.Events.Deployments.Channel;

public class DeleteChannelEvent : IEvent
{
    public DeleteChannelsResponseData Data { get; set; }
}