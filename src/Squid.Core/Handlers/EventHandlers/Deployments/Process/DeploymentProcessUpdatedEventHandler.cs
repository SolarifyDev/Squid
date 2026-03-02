using Squid.Message.Events.Deployments.Process;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Process;

public class DeploymentProcessUpdatedEventHandler : IEventHandler<DeploymentProcessUpdatedEvent>
{
    public Task Handle(IReceiveContext<DeploymentProcessUpdatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
