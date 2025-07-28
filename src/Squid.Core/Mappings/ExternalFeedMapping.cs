using AutoMapper;
using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Mappings;

public class ExternalFeedMapping : Profile
{
    public ExternalFeedMapping()
    {
        CreateMap<ExternalFeed, ExternalFeedDto>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.IsNullOrEmpty(y.PackageAcquisitionLocationOptions) ? new List<string>() : y.PackageAcquisitionLocationOptions.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()));
        CreateMap<ExternalFeedDto, ExternalFeed>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.Join(',', y.PackageAcquisitionLocationOptions ?? new List<string>())));

        CreateMap<CreateExternalFeedCommand, ExternalFeed>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.Join(',', y.PackageAcquisitionLocationOptions ?? new List<string>())));
        
        CreateMap<UpdateExternalFeedCommand, ExternalFeed>()
            .ForMember(x => x.PackageAcquisitionLocationOptions, x => x.MapFrom(y => string.Join(',', y.PackageAcquisitionLocationOptions ?? new List<string>())))
            .ForMember(x => x.Password, x => x.MapFrom(y => y.PasswordNewValue));
    }
}
