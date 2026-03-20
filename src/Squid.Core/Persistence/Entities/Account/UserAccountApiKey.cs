namespace Squid.Core.Persistence.Entities.Account;

public class UserAccountApiKey : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int UserAccountId { get; set; }

    public string ApiKey { get; set; }

    public string? Description { get; set; }

    public bool IsDisabled { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
