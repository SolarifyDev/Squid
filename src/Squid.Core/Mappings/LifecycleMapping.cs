using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.LifeCycle;

namespace Squid.Core.Mappings;

public class LifecycleMapping : Profile
{
    public LifecycleMapping()
    {
        CreateMap<Lifecycle, LifeCycleDto>().ReverseMap();
        CreateMap<LifecyclePhase, LifecyclePhaseDto>()
            .ForMember(x => x.AutomaticDeploymentTargetIds, x => x.Ignore())
            .ForMember(x => x.OptionalDeploymentTargetIds, x => x.Ignore());
        CreateMap<LifecyclePhaseDto, LifecyclePhase>();

        CreateMap<LifeCycleModel, Lifecycle>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.DataVersion, opt => opt.Ignore());

        CreateMap<LifecyclePhaseModel, LifecyclePhase>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.LifecycleId, opt => opt.Ignore());
    }
}
