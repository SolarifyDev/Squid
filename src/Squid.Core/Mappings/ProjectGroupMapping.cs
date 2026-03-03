using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.ProjectGroup;

namespace Squid.Core.Mappings;

public class ProjectGroupMapping : Profile
{
    public ProjectGroupMapping()
    {
        CreateMap<ProjectGroup, ProjectGroupDto>().ReverseMap();

        CreateMap<CreateOrUpdateProjectGroupModel, ProjectGroup>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.DataVersion, opt => opt.Ignore());
    }
}
