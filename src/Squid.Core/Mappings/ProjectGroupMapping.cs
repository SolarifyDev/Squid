using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.ProjectGroup;

namespace Squid.Core.Mappings;

public class ProjectGroupMapping : Profile
{
    public ProjectGroupMapping()
    {
        CreateMap<ProjectGroup, ProjectGroupDto>().ReverseMap();
    }
}
