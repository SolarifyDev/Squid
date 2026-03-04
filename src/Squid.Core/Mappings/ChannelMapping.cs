using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Channel;

namespace Squid.Core.Mappings;

public class ChannelMapping : Profile
{
    public ChannelMapping()
    {
        CreateMap<Channel, ChannelDto>().ReverseMap();

        CreateMap<CreateOrUpdateChannelModel, Channel>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.DataVersion, opt => opt.Ignore());
    }
}