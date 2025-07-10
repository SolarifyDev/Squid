using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Message.Events.Deployments.LifeCycle;

public class LifeCycleUpdatedEvent : IEvent
{
    public UpdateLifeCycleResponseData Data { get; set; }
}