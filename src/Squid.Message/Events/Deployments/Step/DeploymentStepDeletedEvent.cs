using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Message.Events.Deployments.Step;

public class DeploymentStepDeletedEvent : IEvent
{
    public DeleteDeploymentStepResponseData Data { get; set; }
}

