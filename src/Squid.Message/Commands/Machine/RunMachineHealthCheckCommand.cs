using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

public class RunMachineHealthCheckCommand : ICommand
{
    public int MachineId { get; set; }
}

public class RunMachineHealthCheckResponse : SquidResponse
{
}
