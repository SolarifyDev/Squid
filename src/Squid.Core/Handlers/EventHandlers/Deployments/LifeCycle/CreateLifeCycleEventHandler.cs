using Squid.Message.Events.Deployments.LifeCycle;

namespace Squid.Core.Handlers.EventHandlers.Deployments.LifeCycle;

public class CreateLifeCycleEventHandler : IEventHandler<CreateLifeCycleEvent>
{
    public async Task Handle(IReceiveContext<CreateLifeCycleEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}