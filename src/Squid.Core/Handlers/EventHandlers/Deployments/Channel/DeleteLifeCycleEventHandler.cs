using Squid.Message.Events.Deployments.Channel;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Channel;

public class DeleteChannelEventHandler : IEventHandler<ChannelDeletedEvent>
{
    public async Task Handle(IReceiveContext<ChannelDeletedEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}