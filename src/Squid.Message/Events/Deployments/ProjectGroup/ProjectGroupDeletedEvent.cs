using Squid.Message.Commands.Deployments.ProjectGroup;

namespace Squid.Message.Events.Deployments.ProjectGroup;

public class ProjectGroupDeletedEvent : IEvent
{
    public DeleteProjectGroupsResponseData Data { get; set; }
}
