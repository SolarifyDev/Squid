using Squid.Message.Events.Deployments.Step;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Step;

public class DeploymentStepDeletedEventHandler : IEventHandler<DeploymentStepDeletedEvent>
{
    public Task Handle(IReceiveContext<DeploymentStepDeletedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
