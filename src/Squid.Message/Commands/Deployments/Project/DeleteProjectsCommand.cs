using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

[RequiresPermission(Permission.ProjectDelete)]
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

