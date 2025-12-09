using Squid.Message.Models.Deployments.Project;

namespace Squid.Core.Mappings;

public class ProjectMapping : Profile
{
    public ProjectMapping()
    {
        CreateMap<Project, ProjectDto>().ReverseMap();

        CreateMap<ProjectGroup, ProjectGroupDto>().ReverseMap();
    }
}