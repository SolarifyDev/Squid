namespace Squid.Message.Events.Deployments.Machine
{
    public class MachineCreatedEvent : IEvent
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
    }
} 