using Squid.Core.Commands.Deployments.LifeCycle;

namespace Squid.Core.Events.Deployments.LifeCycle;

public class CreateLifeCycleEvent : IEvent
{
    public CreateLifeCycleResponseData Data { get; set; }
}