using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ServerTask;

[RequiresPermission(Permission.TaskCancel)]
public class CancelServerTaskCommand : ICommand
{
    public int TaskId { get; set; }
}

public class CancelServerTaskResponse : SquidResponse;
