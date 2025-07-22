using Squid.Message.Models.Deployments.ExternalFeed;

namespace Squid.Message.Events.Deployments.ExternalFeed;

public class ExternalFeedUpdatedEvent : IEvent
{
    public ExternalFeedDto Data { get; set; }
} 