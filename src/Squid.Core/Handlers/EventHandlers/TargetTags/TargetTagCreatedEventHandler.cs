using Squid.Message.Events.TargetTag;

namespace Squid.Core.Handlers.EventHandlers.TargetTags;

public class TargetTagCreatedEventHandler : IEventHandler<TargetTagCreatedEvent>
{
    public Task Handle(IReceiveContext<TargetTagCreatedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
