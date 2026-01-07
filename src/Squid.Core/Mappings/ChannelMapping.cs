using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Channel;

namespace Squid.Core.Mappings;

public class ChannelMapping : Profile
{
    public ChannelMapping()
    {
        CreateMap<Channel, ChannelDto>().ReverseMap();
    }
}