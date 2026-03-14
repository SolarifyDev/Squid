using Squid.Message.Events.Deployments.Step;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Step;

public class DeploymentStepsReorderedEventHandler : IEventHandler<DeploymentStepsReorderedEvent>
{
    public Task Handle(IReceiveContext<DeploymentStepsReorderedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
