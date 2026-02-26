using Squid.Message.Commands.TargetTag;

namespace Squid.Message.Events.TargetTag;

public class TargetTagDeletedEvent : IEvent
{
    public DeleteTargetTagsResponseData Data { get; set; }
}
