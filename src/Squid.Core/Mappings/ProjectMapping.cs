using Squid.Message.Models.Deployments.Project;
using Project = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.Core.Mappings;

public class ProjectMapping : Profile
{
    public ProjectMapping()
    {
        CreateMap<Project, ProjectDto>().ReverseMap();

        CreateMap<CreateOrUpdateProjectModel, Project>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.DataVersion, opt => opt.Ignore())
            .ForMember(dest => dest.VariableSetId, opt => opt.Ignore())
            .ForMember(dest => dest.DeploymentProcessId, opt => opt.Ignore())
            .ForMember(dest => dest.LastModified, opt => opt.Ignore());
    }
}
