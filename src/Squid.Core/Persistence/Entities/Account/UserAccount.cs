namespace Squid.Core.Persistence.Entities.Account;

public class UserAccount : IEntity<int>
{
    public int Id { get; set; }

    public string UserName { get; set; }

    public string NormalizedUserName { get; set; }

    public string DisplayName { get; set; }

    public string PasswordHash { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsSystem { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
