using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

public class DeleteMachinePolicyCommand : ICommand
{
    public int Id { get; set; }
}

public class DeleteMachinePolicyResponse : SquidResponse
{
}
