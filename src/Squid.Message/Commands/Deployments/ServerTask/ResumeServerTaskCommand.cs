using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ServerTask;

[RequiresPermission(Permission.TaskCreate)]
public class ResumeServerTaskCommand : ICommand
{
    public int TaskId { get; set; }
}

public class ResumeServerTaskResponse : SquidResponse;
