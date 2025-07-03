using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Message.Events.Deployments.LifeCycle;

public class DeleteLifeCycleEvent : IEvent
{
    public DeleteLifeCyclesResponseData Data { get; set; }
}