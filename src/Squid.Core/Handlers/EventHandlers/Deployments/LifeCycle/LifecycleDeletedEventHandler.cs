using Squid.Message.Events.Deployments.LifeCycle;

namespace Squid.Core.Handlers.EventHandlers.Deployments.LifeCycle;

public class LifecycleDeletedEventHandler : IEventHandler<LifeCycleDeletedEvent>
{
    public async Task Handle(IReceiveContext<LifeCycleDeletedEvent> context, CancellationToken cancellationToken)
    {
    }
}