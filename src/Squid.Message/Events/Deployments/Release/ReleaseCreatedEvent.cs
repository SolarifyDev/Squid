using Squid.Message.Models.Deployments.Release;

namespace Squid.Message.Events.Deployments.Release;

public class ReleaseCreatedEvent : IEvent
{
    public ReleaseDto Release { get; set; }
}