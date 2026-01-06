using Squid.Message.Models.Deployments.Project;
using Project = Squid.Core.Persistence.Data.Domain.Deployments.Project;

namespace Squid.Core.Mappings;

public class ProjectMapping : Profile
{
    public ProjectMapping()
    {
        CreateMap<Project, ProjectDto>().ReverseMap();
    }
}
