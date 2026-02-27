using Squid.Message.Models.Deployments.ProjectGroup;

namespace Squid.Message.Events.Deployments.ProjectGroup;

public class ProjectGroupCreatedEvent : IEvent
{
    public ProjectGroupDto Data { get; set; }
}
