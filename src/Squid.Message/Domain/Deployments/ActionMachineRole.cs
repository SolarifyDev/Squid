namespace Squid.Message.Domain.Deployments;

public class ActionMachineRole : IEntity
{
    public int ActionId { get; set; }

    public string MachineRole { get; set; }
}
