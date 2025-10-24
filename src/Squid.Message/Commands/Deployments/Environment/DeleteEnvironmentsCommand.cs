using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Environment;

public class DeleteEnvironmentsCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteEnvironmentsResponse : SquidResponse<DeleteEnvironmentsResponseData>
{
}

public class DeleteEnvironmentsResponseData
{
    public List<int> FailIds { get; set; }
}
