using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.ExternalFeed;

namespace Squid.Core.Mappings;

public class ExternalFeedMapping : Profile
{
    public ExternalFeedMapping()
    {
        CreateMap<ExternalFeed, ExternalFeedDto>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.IsNullOrEmpty(y.PackageAcquisitionLocationOptions) ? new List<string>() : y.PackageAcquisitionLocationOptions.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()))
            .ForMember(x => x.Properties, x => x.MapFrom(y => ExternalFeedProperties.ParseAll(y)));
        CreateMap<ExternalFeedDto, ExternalFeed>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.Join(',', y.PackageAcquisitionLocationOptions ?? new List<string>())))
            .ForMember(x => x.Properties, x => x.MapFrom(y => ExternalFeedProperties.Serialize(y.Properties)));

        CreateMap<CreateExternalFeedCommand, ExternalFeed>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.Join(',', y.PackageAcquisitionLocationOptions ?? new List<string>())))
            .ForMember(x => x.Properties, x => x.MapFrom(y => ExternalFeedProperties.Serialize(y.Properties)));

        CreateMap<UpdateExternalFeedCommand, ExternalFeed>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.Join(',', y.PackageAcquisitionLocationOptions ?? new List<string>())))
            .ForMember(x => x.Password, x => x.MapFrom(y => y.PasswordNewValue))
            .ForMember(x => x.Properties, x => x.MapFrom(y => ExternalFeedProperties.Serialize(y.Properties)));
    }
}
