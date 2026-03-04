using Squid.Message.Events.Deployments.Release;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Release;

public class ReleaseUpdatedEventHandler : IEventHandler<ReleaseUpdatedEvent>
{
    public Task Handle(IReceiveContext<ReleaseUpdatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
