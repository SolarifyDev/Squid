using Squid.Core.Persistence.Data.Domain.Deployments;
using Squid.Message.Models.Deployments.Channel;

namespace Squid.Core.Mappings;

public class ChannelMapping : Profile
{
    public ChannelMapping()
    {
        CreateMap<Channel, ChannelDto>().ReverseMap();
    }
}