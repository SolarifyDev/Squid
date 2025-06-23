using Squid.Message.Events.Deployments.LifeCycle;

namespace Squid.Core.Handlers.EventHandlers.Deployments.LifeCycle;

public class DeleteLifeCycleEventHandler : IEventHandler<DeleteLifeCycleEvent>
{
    public async Task Handle(IReceiveContext<DeleteLifeCycleEvent> context, CancellationToken cancellationToken)
    {
        return;
    }
}