using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

public class DeleteProjectsCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteProjectsResponse : SquidResponse<DeleteProjectsResponseData>
{
}

public class DeleteProjectsResponseData
{
    public List<int> FailIds { get; set; }
}

