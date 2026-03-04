using Squid.Message.Commands.Machine;

namespace Squid.Message.Events.Machine;

public class MachineDeletedEvent : IEvent
{
    public DeleteMachinesResponseData Data { get; set; }
}
