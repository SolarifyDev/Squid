using Squid.Message.Events.Deployments.Machine;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Machine
{
    public class UpdateMachineEventHandler : IEventHandler<MachineUpdatedEvent>
    {
        public Task Handle(IReceiveContext<MachineUpdatedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
} 