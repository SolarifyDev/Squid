using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Models.Deployments.Environment;
using Environment = Squid.Message.Domain.Deployments.Environment;

namespace Squid.Core.Mappings;

public class EnvironmentMapping : Profile
{
    public EnvironmentMapping()
    {
        CreateMap<Environment, EnvironmentDto>();
        CreateMap<EnvironmentDto, Environment>();

        CreateMap<CreateEnvironmentCommand, Environment>();

        CreateMap<UpdateEnvironmentCommand, Environment>();
    }
}
