using Squid.Message.Events.Deployments.Step;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Step;

public class DeploymentStepUpdatedEventHandler : IEventHandler<DeploymentStepUpdatedEvent>
{
    public Task Handle(IReceiveContext<DeploymentStepUpdatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
