namespace Squid.Message.Models.Account;

public class UserAccountDto : IBaseModel
{
    public int Id { get; set; }

    public string UserName { get; set; }

    public string DisplayName { get; set; }

    public bool IsSystem { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
