using Squid.Message.Events.Deployments.LifeCycle;

namespace Squid.Core.Handlers.EventHandlers.Deployments.LifeCycle;

public class LifecycleCreatedEventHandler : IEventHandler<LifeCycleCreateEvent>
{
    public async Task Handle(IReceiveContext<LifeCycleCreateEvent> context, CancellationToken cancellationToken)
    {
    }
}