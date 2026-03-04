using Squid.Message.Models.Deployments.ProjectGroup;

namespace Squid.Message.Events.Deployments.ProjectGroup;

public class ProjectGroupUpdatedEvent : IEvent
{
    public ProjectGroupDto Data { get; set; }
}
