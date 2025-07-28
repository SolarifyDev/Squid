using Squid.Message.Commands.Deployments.ExternalFeed;

namespace Squid.Message.Events.Deployments.ExternalFeed;

public class ExternalFeedDeletedEvent : IEvent
{
    public DeleteExternalFeedsResponseData Data { get; set; }
} 