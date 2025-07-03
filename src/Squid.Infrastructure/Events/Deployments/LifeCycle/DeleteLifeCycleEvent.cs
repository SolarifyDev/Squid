using Squid.Core.Commands.Deployments.LifeCycle;

namespace Squid.Core.Events.Deployments.LifeCycle;

public class DeleteLifeCycleEvent : IEvent
{
    public DeleteLifeCyclesResponseData Data { get; set; }
}