using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Deployments.Process;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Mappings;

public class DeploymentProcessMapping : Profile
{
    public DeploymentProcessMapping()
    {
        CreateMap<DeploymentProcess, DeploymentProcessDto>().ReverseMap();
        CreateMap<DeploymentProcessSnapshot, DeploymentProcessSnapshotDto>().ReverseMap();
        
        CreateMap<DeploymentStep, DeploymentStepDto>();
        CreateMap<DeploymentStepDto, DeploymentStep>()
            .ForMember(dest => dest.Id, opt => opt.Ignore());

        CreateMap<DeploymentStepProperty, DeploymentStepPropertyDto>();
        CreateMap<DeploymentStepPropertyDto, DeploymentStepProperty>();

        CreateMap<DeploymentAction, DeploymentActionDto>();
        CreateMap<DeploymentActionDto, DeploymentAction>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.FeedId, opt => opt.Ignore())
            .ForMember(dest => dest.PackageId, opt => opt.Ignore());

        CreateMap<DeploymentActionProperty, DeploymentActionPropertyDto>();
        CreateMap<DeploymentActionPropertyDto, DeploymentActionProperty>();

        CreateMap<CreateOrUpdateDeploymentStepModel, DeploymentStep>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.ProcessId, opt => opt.Ignore())
            .ForMember(dest => dest.StepOrder, opt => opt.Ignore())
            .ForMember(dest => dest.CreatedAt, opt => opt.Ignore());

        CreateMap<StepPropertyModel, DeploymentStepProperty>()
            .ForMember(dest => dest.StepId, opt => opt.Ignore());

        CreateMap<CreateOrUpdateDeploymentActionModel, DeploymentAction>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.StepId, opt => opt.Ignore())
            .ForMember(dest => dest.ActionOrder, opt => opt.Ignore())
            .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.FeedId, opt => opt.Ignore())
            .ForMember(dest => dest.PackageId, opt => opt.Ignore());

        CreateMap<ActionPropertyModel, DeploymentActionProperty>()
            .ForMember(dest => dest.ActionId, opt => opt.Ignore());

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
