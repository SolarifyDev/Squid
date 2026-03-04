using Squid.Message.Events.Machine;

namespace Squid.Core.Handlers.EventHandlers.Machine;

public class MachineDeletedEventHandler : IEventHandler<MachineDeletedEvent>
{
    public Task Handle(IReceiveContext<MachineDeletedEvent> context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
