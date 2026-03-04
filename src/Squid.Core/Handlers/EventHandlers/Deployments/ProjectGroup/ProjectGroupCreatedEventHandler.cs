using Squid.Message.Events.Deployments.ProjectGroup;

namespace Squid.Core.Handlers.EventHandlers.Deployments.ProjectGroup;

public class ProjectGroupCreatedEventHandler : IEventHandler<ProjectGroupCreatedEvent>
{
    public async Task Handle(IReceiveContext<ProjectGroupCreatedEvent> context, CancellationToken cancellationToken)
    {
    }
}
