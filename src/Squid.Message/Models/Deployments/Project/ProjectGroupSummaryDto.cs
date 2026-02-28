using Squid.Message.Models.Deployments.ProjectGroup;

namespace Squid.Message.Models.Deployments.Project;

public class ProjectGroupSummaryDto
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Slug { get; set; }
    public List<ProjectDto> Projects { get; set; }
    public List<int> EnvironmentIds { get; set; }
}
