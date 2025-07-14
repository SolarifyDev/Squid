namespace Squid.Message.Events.Deployments.Machine
{
    public class MachineUpdatedEvent : IEvent
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
    }
} 