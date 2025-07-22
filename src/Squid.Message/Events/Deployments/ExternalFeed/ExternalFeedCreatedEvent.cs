using Squid.Message.Models.Deployments.ExternalFeed;

namespace Squid.Message.Events.Deployments.ExternalFeed;

public class ExternalFeedCreatedEvent : IEvent
{
    public ExternalFeedDto Data { get; set; }
} 