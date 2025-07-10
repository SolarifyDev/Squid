using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Message.Events.Deployments.LifeCycle;

public class LifeCycleCreateEvent : IEvent
{
    public CreateLifeCycleResponseData Data { get; set; }
}