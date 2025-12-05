using Squid.Message.Models.Deployments.Process;

namespace Squid.Message.Events.Deployments.Step;

public class DeploymentStepUpdatedEvent : IEvent
{
    public DeploymentStepDto Data { get; set; }
}

