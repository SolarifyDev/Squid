using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

public class CreateLifeCycleCommand : ICommand
{
    public LifecycleDetailDto LifecyclePhase { get; set; }
}

public class CreateLifeCycleResponse : SquidResponse<CreateLifeCycleResponseData>
{
}

public class CreateLifeCycleResponseData
{
    public LifecycleDetailDto LifecyclePhase { get; set; }
}