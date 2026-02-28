using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Project;

public class GetProjectSummariesRequest : IRequest
{
    public List<int> ProjectGroupIds { get; set; }
    public List<int> ProjectIds { get; set; }
    public List<int> EnvironmentIds { get; set; }
}

public class GetProjectSummariesResponse : SquidResponse<GetProjectSummariesResponseData>
{
}

public class GetProjectSummariesResponseData
{
    public List<ProjectGroupSummaryDto> Groups { get; set; }
    public List<ProjectDashboardEnvironmentDto> Environments { get; set; }
    public List<ProjectDashboardItemDto> Items { get; set; }
}
