using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineDelete)]
public class DeleteMachinePolicyCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
}

public class DeleteMachinePolicyResponse : SquidResponse
{
}
