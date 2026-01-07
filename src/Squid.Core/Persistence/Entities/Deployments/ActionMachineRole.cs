namespace Squid.Core.Persistence.Entities.Deployments;

public class ActionMachineRole : IEntity
{
    public int ActionId { get; set; }

    public string MachineRole { get; set; }
}
