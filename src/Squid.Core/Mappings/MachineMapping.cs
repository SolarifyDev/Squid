using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Mappings;

public class MachineMapping : Profile
{
    public MachineMapping()
    {
        CreateMap<Machine, MachineDto>().ReverseMap();
    }
} 