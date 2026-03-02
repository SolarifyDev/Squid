using Squid.Message.Events.Deployments.Step;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Step;

public class DeploymentStepCreatedEventHandler : IEventHandler<DeploymentStepCreatedEvent>
{
    public Task Handle(IReceiveContext<DeploymentStepCreatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
