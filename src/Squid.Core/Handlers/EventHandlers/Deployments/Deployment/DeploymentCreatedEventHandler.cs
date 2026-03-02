using Squid.Message.Events.Deployments.Deployment;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Deployment;

public class DeploymentCreatedEventHandler : IEventHandler<DeploymentCreatedEvent>
{
    public Task Handle(IReceiveContext<DeploymentCreatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
