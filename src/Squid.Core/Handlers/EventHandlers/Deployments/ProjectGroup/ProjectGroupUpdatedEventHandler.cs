using Squid.Message.Events.Deployments.ProjectGroup;

namespace Squid.Core.Handlers.EventHandlers.Deployments.ProjectGroup;

public class ProjectGroupUpdatedEventHandler : IEventHandler<ProjectGroupUpdatedEvent>
{
    public async Task Handle(IReceiveContext<ProjectGroupUpdatedEvent> context, CancellationToken cancellationToken)
    {
    }
}
