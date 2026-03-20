namespace Squid.Core.Persistence.Entities.Deployments;

public class LibraryVariableSet : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; }

    public int VariableSetId { get; set; }

    public string ContentType { get; set; }

    public string Json { get; set; }

    public int SpaceId { get; set; }

    public int Version { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
