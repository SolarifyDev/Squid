using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

[RequiresPermission(Permission.LifecycleEdit)]
public class UpdateLifeCycleCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }
    public CreateOrUpdateLifeCycleModel LifecyclePhase { get; set; }
    int? ISpaceScoped.SpaceId => LifecyclePhase?.Lifecycle?.SpaceId;
}

public class UpdateLifeCycleResponse : SquidResponse<UpdateLifeCycleResponseData>
{
}

public class UpdateLifeCycleResponseData
{
    public LifecycleDetailDto LifecyclePhase { get; set; }
}