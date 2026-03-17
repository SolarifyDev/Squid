namespace Squid.Core.Persistence.Entities.Account;

public class Team : IEntity<int>, IAuditable
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public int SpaceId { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
