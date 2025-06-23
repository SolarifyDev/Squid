using Squid.Core.Commands.Deployments.LifeCycle;

namespace Squid.Core.Events.Deployments.LifeCycle;

public class UpdateLifeCycleEvent : IEvent
{
    public UpdateLifeCycleResponseData Data { get; set; }
}