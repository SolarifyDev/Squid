using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Project;

public class GetProjectGroupsWithProjectsRequest : IRequest
{
    public int SpaceId { get; set; }
    
    public string KeyWord { get; set; }
    
    public int ProjectGroupId { get; set; }
    
    public int ProjectId { get; set; }
    
    public int EnvironmentId { get; set; }
}

public class GetProjectGroupsWithProjectsResponse : SquidResponse<List<ProjectGroupsWithProjectsData>>
{
}

public class ProjectGroupsWithProjectsData
{
    public ProjectGroupDto ProjectGroup { get; set; }
    
    public List<Domain.Deployments.Project> Projects { get; set; }
}