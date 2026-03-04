using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ProjectGroup;

public class DeleteProjectGroupsCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteProjectGroupsResponse : SquidResponse<DeleteProjectGroupsResponseData>
{
}

public class DeleteProjectGroupsResponseData
{
    public List<int> FailIds { get; set; }
}
