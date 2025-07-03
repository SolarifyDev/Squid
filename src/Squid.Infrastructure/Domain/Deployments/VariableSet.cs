namespace Squid.Core.Domain.Deployments;

public class VariableSet : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public string OwnerType { get; set; }
    
    public Guid OwnerId { get; set; }
    
    public int Version { get; set; }
    
    public bool IsFrozen { get; set; }
    
    public string Json { get; set; }
    
    public string RelatedDocumentIds { get; set; }
    
    public Guid SpaceId { get; set; }
}
