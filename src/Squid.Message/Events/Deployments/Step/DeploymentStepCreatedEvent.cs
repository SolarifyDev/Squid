using Squid.Message.Models.Deployments.Process;

namespace Squid.Message.Events.Deployments.Step;

public class DeploymentStepCreatedEvent : IEvent
{
    public DeploymentStepDto Data { get; set; }
}

