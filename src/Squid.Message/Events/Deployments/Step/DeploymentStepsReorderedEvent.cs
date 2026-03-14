using Squid.Message.Models.Deployments.Process;

namespace Squid.Message.Events.Deployments.Step;

public class DeploymentStepsReorderedEvent : IEvent
{
    public List<DeploymentStepDto> Data { get; set; }
}
