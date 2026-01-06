using Squid.Message.Domain;

namespace Squid.Core.Persistence.Data.Domain.Deployments;

public class ActionMachineRole : IEntity
{
    public int ActionId { get; set; }

    public string MachineRole { get; set; }
}
