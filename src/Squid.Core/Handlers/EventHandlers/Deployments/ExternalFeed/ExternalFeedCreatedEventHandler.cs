using Squid.Message.Events.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.EventHandlers.Deployments.ExternalFeed
{
    public class ExternalFeedCreatedEventHandler : IEventHandler<ExternalFeedCreatedEvent>
    {
        public Task Handle(IReceiveContext<ExternalFeedCreatedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
}
