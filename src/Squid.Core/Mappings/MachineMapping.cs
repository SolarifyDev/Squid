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

    private static List<int> DeserializeIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>();
        }
        catch (JsonException)
        {
            var result = new List<int>();

            foreach (var segment in json.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(segment, out var id))
                    result.Add(id);
            }

            return result;
        }
    }

    private static List<string> DeserializeRoles(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return json.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }
}
