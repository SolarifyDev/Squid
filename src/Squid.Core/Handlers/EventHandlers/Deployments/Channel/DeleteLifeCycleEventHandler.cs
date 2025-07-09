using Squid.Message.Events.Deployments.Channel;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Channel;

public class DeleteChannelEventHandler : IEventHandler<DeleteChannelEvent>
{
    public async Task Handle(IReceiveContext<DeleteChannelEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}