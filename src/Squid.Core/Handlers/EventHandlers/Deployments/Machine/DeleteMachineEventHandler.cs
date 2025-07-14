using Squid.Message.Events.Deployments.Machine;

namespace Squid.Core.Handlers.EventHandlers.Deployments.Machine
{
    public class DeleteMachineEventHandler : IEventHandler<MachineDeletedEvent>
    {
        public Task Handle(IReceiveContext<MachineDeletedEvent> context, CancellationToken cancellationToken)
        {
            // 可扩展副作用逻辑
            return Task.CompletedTask;
        }
    }
} 