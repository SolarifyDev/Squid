using Squid.Message.Events.Deployments.Channel;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Channel;

public class CreateChannelEventHandler : IEventHandler<ChannelCreatedEvent>
{
    public async Task Handle(IReceiveContext<ChannelCreatedEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}