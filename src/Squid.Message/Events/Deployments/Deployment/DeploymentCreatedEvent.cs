using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Message.Events.Deployments.Deployment;

public class DeploymentCreatedEvent : IEvent
{
    public DeploymentDto Deployment { get; set; }

    public int TaskId { get; set; }
}
