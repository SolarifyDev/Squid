using Squid.Message.Models.Deployments;
using Squid.Message.Models.Deployments.LifeCycle;

namespace Squid.Core.Mappings;

public class LifecycleMapping : Profile
{
    public LifecycleMapping()
    {
        CreateMap<Lifecycle, LifeCycleDto>().ReverseMap();
        CreateMap<Phase, PhaseDto>()
            .ForMember(x => x.AutomaticDeploymentTargets, x => x.MapFrom(y => y.AutomaticDeploymentTargets.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()))
            .ForMember(x => x.OptionalDeploymentTargets, x => x.MapFrom(y => y.OptionalDeploymentTargets.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()));
        CreateMap<PhaseDto, Phase>()
            .ForMember(x => x.AutomaticDeploymentTargets, x => x.MapFrom(y => string.Join(',', y.AutomaticDeploymentTargets)))
            .ForMember(x => x.OptionalDeploymentTargets, x => x.MapFrom(y => string.Join(',', y.OptionalDeploymentTargets)));
        CreateMap<RetentionPolicy, RetentionPolicyDto>().ReverseMap();
    }
}