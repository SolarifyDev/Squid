using Squid.Message.Events.Deployments.ProjectGroup;

namespace Squid.Core.Handlers.EventHandlers.Deployments.ProjectGroup;

public class ProjectGroupDeletedEventHandler : IEventHandler<ProjectGroupDeletedEvent>
{
    public async Task Handle(IReceiveContext<ProjectGroupDeletedEvent> context, CancellationToken cancellationToken)
    {
    }
}
