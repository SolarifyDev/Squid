using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Models.Deployments.Release;

namespace Squid.Core.Mappings;

public class ReleaseMapping : Profile
{
    public ReleaseMapping()
    {
        CreateMap<Release, ReleaseDto>().ReverseMap();

        CreateMap<CreateReleaseCommand, Release>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.Assembled, opt => opt.Ignore())
            .ForMember(dest => dest.ProjectVariableSetSnapshotId, opt => opt.Ignore())
            .ForMember(dest => dest.ProjectDeploymentProcessSnapshotId, opt => opt.Ignore())
            .ForMember(dest => dest.SpaceId, opt => opt.Ignore())
            .ForMember(dest => dest.LastModified, opt => opt.Ignore());

        CreateMap<UpdateReleaseModel, Release>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.Assembled, opt => opt.Ignore())
            .ForMember(dest => dest.ProjectId, opt => opt.Ignore())
            .ForMember(dest => dest.ProjectVariableSetSnapshotId, opt => opt.Ignore())
            .ForMember(dest => dest.ProjectDeploymentProcessSnapshotId, opt => opt.Ignore())
            .ForMember(dest => dest.ChannelId, opt => opt.Ignore())
            .ForMember(dest => dest.SpaceId, opt => opt.Ignore())
            .ForMember(dest => dest.LastModified, opt => opt.Ignore());
    }
}
