using Squid.Message.Models.Deployments.Machine;

namespace Squid.Message.Events.Deployments.Machine;

public class MachineUpdatedEvent : IEvent
{
    public MachineDto Data { get; set; }
} 