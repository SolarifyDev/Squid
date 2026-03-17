using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

[RequiresPermission(Permission.LifecycleCreate)]
public class CreateLifeCycleCommand : ICommand
{
    public CreateOrUpdateLifeCycleModel LifecyclePhase { get; set; }
}

public class CreateLifeCycleResponse : SquidResponse<CreateLifeCycleResponseData>
{
}

public class CreateLifeCycleResponseData
{
    public LifecycleDetailDto LifecyclePhase { get; set; }
}