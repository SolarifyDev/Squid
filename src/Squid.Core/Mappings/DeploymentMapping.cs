using Squid.Message.Models.Deployments.Deployment;
using Deployment = Squid.Core.Persistence.Entities.Deployments.Deployment;

namespace Squid.Core.Mappings;

public class DeploymentMapping : Profile
{
    public DeploymentMapping()
    {
        CreateMap<Deployment, DeploymentDto>()
            .ForMember(dest => dest.VariableSnapshotId, opt => opt.MapFrom(src => src.VariableSetSnapshotId));
    }
}
