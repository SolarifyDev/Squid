using Squid.Message.Models.Deployments.TargetTag;

namespace Squid.Message.Events.TargetTag;

public class TargetTagCreatedEvent : IEvent
{
    public TargetTagDto Data { get; set; }
}
