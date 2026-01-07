using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Mappings;

public class VariableMapping : Profile
{
    public VariableMapping()
    {
        CreateMap<VariableDto, Variable>().ReverseMap();

        CreateMap<VariableScope, VariableScopeDto>();
        CreateMap<VariableScopeDto, VariableScope>()
            .ForMember(dest => dest.Id, opt => opt.Ignore());

        CreateMap<VariableSet, VariableSetDto>().ReverseMap();

        CreateMap<CreateVariableSetCommand, VariableSet>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.Version, opt => opt.MapFrom(src => 1))
            .ForMember(dest => dest.LastModified, opt => opt.MapFrom(src => DateTimeOffset.UtcNow));
    }
}
