namespace Squid.Core.Persistence.Entities.Account;

public class UserAccountApiKey : IEntity<int>
{
    public int Id { get; set; }

    public int UserAccountId { get; set; }

    public string ApiKey { get; set; }

    public string? Description { get; set; }

    public bool IsDisabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
