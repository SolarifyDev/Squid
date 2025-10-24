namespace Squid.Message.Domain.Deployments;

public class LibraryVariableSet : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public Guid VariableSetId { get; set; }
    
    public string ContentType { get; set; }
    
    public string Json { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public int Version { get; set; }
}
