using Squid.Message.Domain.Deployments;

namespace Squid.Message.Events.Deployments.Project;

public class ProjectGroupCreatedEvent : IEvent
{
    public ProjectGroup ProjectGroup { get; set; }
}