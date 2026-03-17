namespace Squid.Core.Persistence.Entities.Account;

public class UserRolePermission : IEntity
{
    public int UserRoleId { get; set; }
    public string Permission { get; set; }
}
