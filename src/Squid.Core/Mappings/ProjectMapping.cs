using Squid.Message.Models.Deployments.Project;
using Project = Squid.Message.Domain.Deployments.Project;

namespace Squid.Core.Mappings;

public class ProjectMapping : Profile
{
    public ProjectMapping()
    {
        CreateMap<Project, ProjectDto>().ReverseMap();
    }
}
