using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Spaces;
using Squid.Message.Models.Spaces;

namespace Squid.Core.Mappings;

public class SpaceMapping : Profile
{
    public SpaceMapping()
    {
        CreateMap<Space, SpaceDto>();
        CreateMap<CreateSpaceCommand, Space>()
            .ForSourceMember(s => s.OwnerTeamIds, o => o.DoNotValidate())
            .ForSourceMember(s => s.OwnerUserIds, o => o.DoNotValidate());
        CreateMap<UpdateSpaceCommand, Space>()
            .ForSourceMember(s => s.OwnerTeamIds, o => o.DoNotValidate())
            .ForSourceMember(s => s.OwnerUserIds, o => o.DoNotValidate());
    }
}
