namespace Squid.Core.Commands.Customers;

public class DeleteCustomerCommand : ICommand
{
    public Guid CustomerId { get; set; }
}