using Squid.Message.Events.Deployments.Channel;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Channel;

public class CreateChannelEventHandler : IEventHandler<CreateChannelEvent>
{
    public async Task Handle(IReceiveContext<CreateChannelEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}