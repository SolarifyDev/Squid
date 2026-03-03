using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

public class UpdateLifeCycleCommand : ICommand
{
    public int Id { get; set; }
    public CreateOrUpdateLifeCycleModel LifecyclePhase { get; set; }
}

public class UpdateLifeCycleResponse : SquidResponse<UpdateLifeCycleResponseData>
{
}

public class UpdateLifeCycleResponseData
{
    public LifecycleDetailDto LifecyclePhase { get; set; }
}