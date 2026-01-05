using Squid.Message.Events.Deployments.Project;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Project;

public class ProjectCreatedEventHandler : IEventHandler<ProjectCreatedEvent>
{
    public Task Handle(IReceiveContext<ProjectCreatedEvent> context, CancellationToken cancellationToken)
    {
        // 可扩展项目创建后的副作用逻辑
        return Task.CompletedTask;
    }
}
