namespace Squid.Core.Persistence.Entities.Account;

public class ScopedUserRole : IEntity<int>, IAuditable
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public int UserRoleId { get; set; }
    public int? SpaceId { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
