using Squid.Message.Events.Deployments.Channel;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Channel;

public class UpdateChannelEventHandler : IEventHandler<UpdateChannelEvent>
{
    public async Task Handle(IReceiveContext<UpdateChannelEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}