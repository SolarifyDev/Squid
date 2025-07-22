using Squid.Message.Models.Deployments.Machine;

namespace Squid.Message.Events.Deployments.Machine;

public class MachineCreatedEvent : IEvent
{
    public MachineDto Data { get; set; }
} 