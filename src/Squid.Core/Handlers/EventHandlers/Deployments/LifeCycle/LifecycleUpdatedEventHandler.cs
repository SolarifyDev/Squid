using Squid.Message.Events.Deployments.LifeCycle;

namespace Squid.Core.Handlers.EventHandlers.Deployments.LifeCycle;

public class LifecycleUpdatedEventHandler : IEventHandler<LifeCycleUpdatedEvent>
{
    public async Task Handle(IReceiveContext<LifeCycleUpdatedEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}