namespace Squid.Message.Events.Deployments.Machine
{
    public class MachineDeletedEvent : IEvent
    {
        public Guid Id { get; set; }
    }
} 