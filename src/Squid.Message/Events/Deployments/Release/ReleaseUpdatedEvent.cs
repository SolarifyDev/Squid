using Squid.Message.Models.Deployments.Release;

namespace Squid.Message.Events.Deployments.Release;

public class ReleaseUpdatedEvent : IEvent
{
    public ReleaseDto Release { get; set; }
}