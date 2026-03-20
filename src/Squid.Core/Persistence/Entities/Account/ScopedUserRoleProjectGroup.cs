namespace Squid.Core.Persistence.Entities.Account;

public class ScopedUserRoleProjectGroup : IEntity
{
    public int ScopedUserRoleId { get; set; }
    public int ProjectGroupId { get; set; }
}
