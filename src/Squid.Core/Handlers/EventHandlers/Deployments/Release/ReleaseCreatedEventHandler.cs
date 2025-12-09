using Squid.Message.Events.Deployments.Release;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Release;

public class ReleaseCreatedEventHandler : IEventHandler<ReleaseCreatedEvent>
{
    public async Task Handle(IReceiveContext<ReleaseCreatedEvent> context, CancellationToken cancellationToken)
    {
    }
}