using System.Text.Json;
using Squid.Message.Models.Deployments.Project;
using Project = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.Core.Mappings;

public class ProjectMapping : Profile
{
    public ProjectMapping()
    {
        CreateMap<Project, ProjectDto>()
            .ForMember(dest => dest.IncludedLibraryVariableSetIds, opt => opt.MapFrom(src => DeserializeIds(src.IncludedLibraryVariableSetIds)));

        CreateMap<ProjectDto, Project>()
            .ForMember(dest => dest.IncludedLibraryVariableSetIds, opt => opt.MapFrom(src => SerializeIds(src.IncludedLibraryVariableSetIds)));

        CreateMap<CreateOrUpdateProjectModel, Project>()
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.DataVersion, opt => opt.Ignore())
            .ForMember(dest => dest.VariableSetId, opt => opt.Ignore())
            .ForMember(dest => dest.DeploymentProcessId, opt => opt.Ignore())
            .ForMember(dest => dest.LastModified, opt => opt.Ignore())
            .ForMember(dest => dest.IncludedLibraryVariableSetIds, opt => opt.MapFrom(src => SerializeIds(src.IncludedLibraryVariableSetIds)));
    }

    private static List<int> DeserializeIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<int>();

        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>();
        }
        catch
        {
            return new List<int>();
        }
    }

    private static string SerializeIds(List<int> ids)
    {
        if (ids == null || ids.Count == 0)
            return "[]";

        return JsonSerializer.Serialize(ids);
    }
}
