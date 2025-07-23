using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Environment;

public class DeleteEnvironmentsCommand : ICommand
{
    public List<Guid> Ids { get; set; }
}

public class DeleteEnvironmentsResponse : SquidResponse<DeleteEnvironmentsResponseData>
{
}

public class DeleteEnvironmentsResponseData
{
    public List<Guid> FailIds { get; set; }
}
