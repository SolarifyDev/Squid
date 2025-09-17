namespace Squid.Message.Domain.Deployments;

public class ActionMachineRole : IEntity
{
    public Guid ActionId { get; set; }

    public string MachineRole { get; set; }
}
