namespace Squid.Core.Persistence.Entities.Deployments;

public class Project : IEntity<int>
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
        return string.IsNullOrWhiteSpace(IncludedLibraryVariableSetIds)
            ? new List<int> { VariableSetId }
            : IncludedLibraryVariableSetIds.Split(',').Select(int.Parse).Concat([VariableSetId]).ToList();
    }
    
    public bool DiscreteChannelRelease { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public int? ClonedFromProjectId { get; set; }
    
    public int SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
    
    public bool AllowIgnoreChannelRules { get; set; }
}
