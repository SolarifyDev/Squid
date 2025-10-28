namespace Squid.Message.Domain.Deployments;

public class Project : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public string Slug { get; set; }
    
    public bool IsDisabled { get; set; }
    
    public int VariableSetId { get; set; }
    
    public Guid DeploymentProcessId { get; set; }
    
    public Guid ProjectGroupId { get; set; }
    
    public Guid LifecycleId { get; set; }
    
    public bool AutoCreateRelease { get; set; }
    
    public string Json { get; set; }
    
    public Guid IncludedLibraryVariableSetIds { get; set; }
    
    public bool DiscreteChannelRelease { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public Guid? ClonedFromProjectId { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
    
    public bool AllowIgnoreChannelRules { get; set; }
}
