using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Machine;

public class DeleteMachineCommand
{
    public Guid Id { get; set; }
}

public class DeleteMachineResponse : SquidResponse<DeleteMachineResponseData>
{
}

public class DeleteMachineResponseData
{
    public bool Success { get; set; }
}