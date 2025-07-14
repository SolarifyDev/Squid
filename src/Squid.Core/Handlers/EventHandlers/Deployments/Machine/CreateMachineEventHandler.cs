using Squid.Message.Events.Deployments.Machine;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Machine
{
    public class CreateMachineEventHandler : IEventHandler<MachineCreatedEvent>
    {
        public Task Handle(IReceiveContext<MachineCreatedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
} 