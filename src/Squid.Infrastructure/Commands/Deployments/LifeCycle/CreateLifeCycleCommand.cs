using Squid.Core.Models.Deployments.LifeCycle;
using Squid.Core.Response;

namespace Squid.Core.Commands.Deployments.LifeCycle;

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