using AutoMapper;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Mappings;

public class EnvironmentMapping : Profile
{
    public EnvironmentMapping()
    {
        CreateMap<Message.Domain.Deployments.Environment, EnvironmentDto>();
        CreateMap<EnvironmentDto, Message.Domain.Deployments.Environment>();

        CreateMap<CreateEnvironmentCommand, Message.Domain.Deployments.Environment>();

        CreateMap<UpdateEnvironmentCommand, Message.Domain.Deployments.Environment>();
    }
}
