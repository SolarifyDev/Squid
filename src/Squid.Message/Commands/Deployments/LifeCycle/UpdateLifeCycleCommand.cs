using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

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