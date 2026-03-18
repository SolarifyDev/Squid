using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class RunMachineHealthCheckCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int MachineId { get; set; }
}

public class RunMachineHealthCheckResponse : SquidResponse
{
}
