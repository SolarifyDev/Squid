using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Machine;

public class DeleteMachinesCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteMachinesResponse : SquidResponse<DeleteMachinesResponseData>
{
}

public class DeleteMachinesResponseData
{
    public List<int> FailIds { get; set; }
}
