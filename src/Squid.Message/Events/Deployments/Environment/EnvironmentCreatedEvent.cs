using Squid.Message.Models.Deployments.Environment;

namespace Squid.Message.Events.Deployments.Environment;

public class EnvironmentCreatedEvent : IEvent
{
    public EnvironmentDto Data { get; set; }
}
