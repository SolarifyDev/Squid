namespace Squid.Core.Persistence.Entities.Account;

public class ScopedUserRoleProject : IEntity
{
    public int ScopedUserRoleId { get; set; }
    public int ProjectId { get; set; }
}
