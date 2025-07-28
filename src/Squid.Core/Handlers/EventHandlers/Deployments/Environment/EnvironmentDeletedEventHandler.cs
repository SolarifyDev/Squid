using Squid.Message.Events.Deployments.Environment;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Environment
{
    public class EnvironmentDeletedEventHandler : IEventHandler<EnvironmentDeletedEvent>
    {
        public Task Handle(IReceiveContext<EnvironmentDeletedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
}
