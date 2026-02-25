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
    }
}
