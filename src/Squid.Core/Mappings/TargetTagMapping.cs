using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.TargetTag;
using Squid.Message.Models.Deployments.TargetTag;

namespace Squid.Core.Mappings;

public class TargetTagMapping : Profile
{
    public TargetTagMapping()
    {
        CreateMap<TargetTag, TargetTagDto>();
        CreateMap<TargetTagDto, TargetTag>();

        CreateMap<CreateTargetTagCommand, TargetTag>();
    }
}
