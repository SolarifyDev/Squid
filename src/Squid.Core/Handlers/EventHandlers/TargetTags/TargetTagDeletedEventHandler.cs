using Squid.Message.Events.TargetTag;

namespace Squid.Core.Handlers.EventHandlers.TargetTags;

public class TargetTagDeletedEventHandler : IEventHandler<TargetTagDeletedEvent>
{
    public Task Handle(IReceiveContext<TargetTagDeletedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
