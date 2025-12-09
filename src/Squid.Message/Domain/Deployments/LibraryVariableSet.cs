namespace Squid.Message.Domain.Deployments;

public class LibraryVariableSet : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public int VariableSetId { get; set; }
    
    public string ContentType { get; set; }
    
    public string Json { get; set; }
    
    public int SpaceId { get; set; }
    
    public int Version { get; set; }
}
