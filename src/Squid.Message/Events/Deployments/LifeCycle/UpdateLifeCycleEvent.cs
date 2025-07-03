using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Message.Events.Deployments.LifeCycle;

public class UpdateLifeCycleEvent : IEvent
{
    public UpdateLifeCycleResponseData Data { get; set; }
}