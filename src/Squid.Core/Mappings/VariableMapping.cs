using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Mappings;

public class VariableMapping : Profile
{
    public VariableMapping()
    {
        CreateMap<Variable, VariableDto>();
        CreateMap<VariableDto, Variable>();

        CreateMap<VariableScope, VariableScopeDto>();
        CreateMap<VariableScopeDto, VariableScope>()
            .ForMember(dest => dest.Id, opt => opt.Ignore());

        CreateMap<VariableSet, VariableSetDto>();
        CreateMap<VariableSetDto, VariableSet>();

        CreateMap<CreateVariableSetCommand, VariableSet>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.Version, opt => opt.MapFrom(src => 1))
            .ForMember(dest => dest.IsFrozen, opt => opt.MapFrom(src => false))
            .ForMember(dest => dest.LastModified, opt => opt.MapFrom(src => DateTimeOffset.UtcNow));

        CreateMap<VariableSnapshotData, Variable>().ReverseMap();
    }
}
