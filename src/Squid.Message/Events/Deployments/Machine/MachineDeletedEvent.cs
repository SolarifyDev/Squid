using Squid.Message.Commands.Deployments.Machine;

namespace Squid.Message.Events.Deployments.Machine
{
    public class MachineDeletedEvent : IEvent
    {
        public DeleteMachinesResponseData Data { get; set; }
    }
} 