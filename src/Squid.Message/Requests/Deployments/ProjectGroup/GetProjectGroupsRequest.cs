using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.ProjectGroup;

[RequiresPermission(Permission.ProjectView)]
public class GetProjectGroupsRequest : IPaginatedRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int PageIndex { get; set; }
    public int PageSize { get; set; }
}

public class GetProjectGroupsResponse : SquidResponse<GetProjectGroupsResponseData>
{
}

public class GetProjectGroupsResponseData
{
    public int Count { get; set; }
    public List<ProjectGroupDto> ProjectGroups { get; set; }
}
