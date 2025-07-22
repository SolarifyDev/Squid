using Squid.Message.Events.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.EventHandlers.Deployments.ExternalFeed
{
    public class ExternalFeedDeletedEventHandler : IEventHandler<ExternalFeedDeletedEvent>
    {
        public Task Handle(IReceiveContext<ExternalFeedDeletedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
}
