using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Spaces;
using Squid.Message.Models.Spaces;

namespace Squid.Core.Mappings;

public class SpaceMapping : Profile
{
    public SpaceMapping()
    {
        CreateMap<Space, SpaceDto>();
        CreateMap<CreateSpaceCommand, Space>();
        CreateMap<UpdateSpaceCommand, Space>();
    }
}
