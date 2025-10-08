using Squid.Message.Models.Deployments.Process;

namespace Squid.Message.Events.Deployments.Process;

public class DeploymentProcessCreatedEvent : IEvent
{
    public DeploymentProcessDto DeploymentProcess { get; set; }
}