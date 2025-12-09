using Squid.Message.Models.Deployments.Project;

namespace Squid.Message.Events.Deployments.Project;

public class ProjectCreatedEvent : IEvent
{
    public ProjectDto Project { get; set; }
}