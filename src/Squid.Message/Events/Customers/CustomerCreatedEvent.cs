﻿namespace Squid.Message.Events.Customers
{
    public class CustomerCreatedEvent : IEvent
    {
        public CustomerCreatedEvent(Guid customerId, string name)
        {
            CustomerId = customerId;
            Name = name;
        }

        public Guid CustomerId { get; }
        public string Name { get; }
    }
}
