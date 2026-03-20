namespace Squid.Core.Persistence.Entities.Account;

public class UserAccount : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string UserName { get; set; }

    public string NormalizedUserName { get; set; }

    public string DisplayName { get; set; }

    public string PasswordHash { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsSystem { get; set; }

    public bool MustChangePassword { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
