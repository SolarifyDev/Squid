using Squid.Message.Events.Deployments.LifeCycle;

namespace Squid.Core.Handlers.EventHandlers.Deployments.LifeCycle;

public class UpdateLifeCycleEventHandler : IEventHandler<UpdateLifeCycleEvent>
{
    public async Task Handle(IReceiveContext<UpdateLifeCycleEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}