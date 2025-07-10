using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Message.Events.Deployments.LifeCycle;

public class LifeCycleDeletedEvent : IEvent
{
    public DeleteLifeCyclesResponseData Data { get; set; }
}