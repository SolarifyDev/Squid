using Squid.Core.Models.Deployments.LifeCycle;
using Squid.Core.Response;

namespace Squid.Core.Commands.Deployments.LifeCycle;

public class UpdateLifeCycleCommand : ICommand
{
    public LifecyclePhaseDto LifecyclePhase { get; set; }
}

public class UpdateLifeCycleResponse : SquidResponse<UpdateLifeCycleResponseData>
{
}

public class UpdateLifeCycleResponseData 
{
    public LifecyclePhaseDto LifecyclePhase { get; set; }
}