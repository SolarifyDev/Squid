using Squid.Core.Persistence.Data.Domain.Deployments;
using Squid.Message.Commands.Deployments.Process;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Mappings;

public class DeploymentProcessMapping : Profile
{
    public DeploymentProcessMapping()
    {
        CreateMap<DeploymentProcess, DeploymentProcessDto>();
        CreateMap<DeploymentProcessDto, DeploymentProcess>();

        CreateMap<DeploymentStep, DeploymentStepDto>();
        CreateMap<DeploymentStepDto, DeploymentStep>()
            .ForMember(dest => dest.Id, opt => opt.Ignore());

        CreateMap<DeploymentStepProperty, DeploymentStepPropertyDto>();
        CreateMap<DeploymentStepPropertyDto, DeploymentStepProperty>();

        CreateMap<DeploymentAction, DeploymentActionDto>();
        CreateMap<DeploymentActionDto, DeploymentAction>()
            .ForMember(dest => dest.Id, opt => opt.Ignore());

        CreateMap<DeploymentActionProperty, DeploymentActionPropertyDto>();
        CreateMap<DeploymentActionPropertyDto, DeploymentActionProperty>();

        CreateMap<CreateDeploymentProcessCommand, DeploymentProcess>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.Version, opt => opt.Ignore())
            .ForMember(dest => dest.LastModified, opt => opt.Ignore())
            .ForMember(dest => dest.LastModifiedBy, opt => opt.Ignore());

        CreateMap<UpdateDeploymentProcessCommand, DeploymentProcess>()
            .ForMember(dest => dest.Version, opt => opt.Ignore())
            .ForMember(dest => dest.SpaceId, opt => opt.Ignore())
            .ForMember(dest => dest.LastModified, opt => opt.Ignore())
            .ForMember(dest => dest.LastModifiedBy, opt => opt.Ignore());
    }
}
