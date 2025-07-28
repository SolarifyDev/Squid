using Squid.Message.Events.Deployments.Environment;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Environment
{
    public class EnvironmentCreatedEventHandler : IEventHandler<EnvironmentCreatedEvent>
    {
        public Task Handle(IReceiveContext<EnvironmentCreatedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
}
