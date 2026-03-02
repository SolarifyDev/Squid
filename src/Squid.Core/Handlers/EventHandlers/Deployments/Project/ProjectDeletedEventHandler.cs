using Squid.Message.Events.Deployments.Project;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Project;

public class ProjectDeletedEventHandler : IEventHandler<ProjectDeletedEvent>
{
    public Task Handle(IReceiveContext<ProjectDeletedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
