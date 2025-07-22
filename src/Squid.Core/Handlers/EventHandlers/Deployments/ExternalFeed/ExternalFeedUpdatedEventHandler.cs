using Squid.Message.Events.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.EventHandlers.Deployments.ExternalFeed
{
    public class ExternalFeedUpdatedEventHandler : IEventHandler<ExternalFeedUpdatedEvent>
    {
        public Task Handle(IReceiveContext<ExternalFeedUpdatedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
}
