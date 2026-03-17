using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class AutoMachineHealthCheckCommand : ICommand
{
}

public class AutoMachineHealthCheckResponse : SquidResponse
{
}
