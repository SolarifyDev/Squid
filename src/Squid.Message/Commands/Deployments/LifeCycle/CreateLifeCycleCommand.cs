using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

public class CreateLifeCycleCommand : ICommand, IBaseModel
{
    public LifecyclePhaseDto LifecyclePhase { get; set; }
}

public class CreateLifeCycleResponse : SquidResponse<CreateLifeCycleResponseData>
{
}

public class CreateLifeCycleResponseData
{
    
    public LifecyclePhaseDto LifecyclePhase { get; set; }
}