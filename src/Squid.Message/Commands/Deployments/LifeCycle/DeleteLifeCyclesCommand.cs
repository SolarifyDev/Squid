using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

[RequiresPermission(Permission.LifecycleDelete)]
public class DeleteLifeCyclesCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteLifeCyclesResponse : SquidResponse<DeleteLifeCyclesResponseData>
{
}

public class DeleteLifeCyclesResponseData
{
    public List<int> FailIds { get; set; }
}
