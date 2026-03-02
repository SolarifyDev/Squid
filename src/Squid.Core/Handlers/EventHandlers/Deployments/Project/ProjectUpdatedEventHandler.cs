using Squid.Message.Events.Deployments.Project;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Project;

public class ProjectUpdatedEventHandler : IEventHandler<ProjectUpdatedEvent>
{
    public Task Handle(IReceiveContext<ProjectUpdatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
