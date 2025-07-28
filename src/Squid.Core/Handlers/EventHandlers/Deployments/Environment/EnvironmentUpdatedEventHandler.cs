using Squid.Message.Events.Deployments.Environment;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Environment
{
    public class EnvironmentUpdatedEventHandler : IEventHandler<EnvironmentUpdatedEvent>
    {
        public Task Handle(IReceiveContext<EnvironmentUpdatedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
}
