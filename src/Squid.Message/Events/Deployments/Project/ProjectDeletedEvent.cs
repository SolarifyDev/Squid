using Squid.Message.Commands.Deployments.Project;

namespace Squid.Message.Events.Deployments.Project;

public class ProjectDeletedEvent : IEvent
{
    public DeleteProjectsResponseData Data { get; set; }
}

