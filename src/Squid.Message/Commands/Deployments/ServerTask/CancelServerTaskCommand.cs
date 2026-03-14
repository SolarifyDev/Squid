using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ServerTask;

public class CancelServerTaskCommand : ICommand
{
    public int TaskId { get; set; }
}

public class CancelServerTaskResponse : SquidResponse;
