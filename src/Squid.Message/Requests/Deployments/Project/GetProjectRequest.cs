using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Project;

[RequiresPermission(Permission.ProjectView)]
public class GetProjectRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
}

public class GetProjectResponse : SquidResponse<ProjectDto>
{
}

