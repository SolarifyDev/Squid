namespace Squid.Core.Persistence.Entities.Account;

public class ScopedUserRoleEnvironment : IEntity
{
    public int ScopedUserRoleId { get; set; }
    public int EnvironmentId { get; set; }
}
