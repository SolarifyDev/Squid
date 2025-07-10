using Squid.Message.Events.Deployments.Channel;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Channel;

public class UpdateChannelEventHandler : IEventHandler<ChannelUpdatedEvent>
{
    public async Task Handle(IReceiveContext<ChannelUpdatedEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}