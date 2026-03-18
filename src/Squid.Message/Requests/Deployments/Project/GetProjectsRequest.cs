using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Project;

[RequiresPermission(Permission.ProjectView)]
public class GetProjectsRequest : IPaginatedRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int PageIndex { get; set; }

    public int PageSize { get; set; }

    public string Keyword { get; set; }
}

public class GetProjectsResponse : SquidResponse<GetProjectsResponseData>
{
}

public class GetProjectsResponseData
{
    public int Count { get; set; }

    public List<ProjectDto> Projects { get; set; }
}

