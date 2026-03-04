using Squid.Message.Events.Deployments.Process;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Process;

public class DeploymentProcessCreatedEventHandler : IEventHandler<DeploymentProcessCreatedEvent>
{
    public Task Handle(IReceiveContext<DeploymentProcessCreatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
