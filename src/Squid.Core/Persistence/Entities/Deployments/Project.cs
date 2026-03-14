using System.Text.Json;

namespace Squid.Core.Persistence.Entities.Deployments;

public class Project : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public bool IsDisabled { get; set; }

    public int VariableSetId { get; set; }

    public int DeploymentProcessId { get; set; }

    public int ProjectGroupId { get; set; }

    public int LifecycleId { get; set; }

    public bool AutoCreateRelease { get; set; }

    public string Json { get; set; }

    public string IncludedLibraryVariableSetIds { get; set; }

    public List<int> GetIncludedLibraryVariableSetIdList()
    {
        if (string.IsNullOrWhiteSpace(IncludedLibraryVariableSetIds))
            return new List<int>();

        try
        {
            return JsonSerializer.Deserialize<List<int>>(IncludedLibraryVariableSetIds) ?? new List<int>();
        }
        catch
        {
            return new List<int>();
        }
    }

    public bool DiscreteChannelRelease { get; set; }

    public byte[] DataVersion { get; set; }

    public int? ClonedFromProjectId { get; set; }

    public int SpaceId { get; set; }

    public bool AllowIgnoreChannelRules { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
