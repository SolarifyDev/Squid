using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Mappings;

public class MachineMapping : Profile
{
    public MachineMapping()
    {
        CreateMap<Machine, MachineDto>()
            .ForMember(x => x.EnvironmentIds, x => x.MapFrom(y =>
                string.IsNullOrEmpty(y.EnvironmentIds) ? new List<int>()
                : DeserializeIds(y.EnvironmentIds)))
            .ForMember(x => x.Roles, x => x.MapFrom(y =>
                string.IsNullOrEmpty(y.Roles) ? new List<string>()
                : DeserializeRoles(y.Roles)));
    }

    private static List<int> DeserializeIds(string json) => JsonSerializer.Deserialize<List<int>>(json);

    private static List<string> DeserializeRoles(string json) => JsonSerializer.Deserialize<List<string>>(json);
}
