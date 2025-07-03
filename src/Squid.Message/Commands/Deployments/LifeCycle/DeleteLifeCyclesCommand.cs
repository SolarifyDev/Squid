using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

public class DeleteLifeCyclesCommand : ICommand
{
    public List<Guid> Ids { get; set; }
}

public class DeleteLifeCyclesResponse : SquidResponse<DeleteLifeCyclesResponseData>
{
}

public class DeleteLifeCyclesResponseData
{
    public List<Guid> FailIds { get; set; }
}