using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Mappings;

public class MachineMapping : Profile
{
    public MachineMapping()
    {
        CreateMap<Machine, MachineDto>().ReverseMap();

        CreateMap<CreateMachineCommand, Machine>();

        CreateMap<UpdateMachineCommand, Machine>();
    }
} 