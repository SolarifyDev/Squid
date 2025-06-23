using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Message.Events.Deployments.LifeCycle;

public class CreateLifeCycleEvent : IEvent
{
    public CreateLifeCycleResponseData Data { get; set; }
}