namespace Squid.Core.Persistence.Entities.Account;

public class TeamMember : IEntity
{
    public int TeamId { get; set; }
    public int UserId { get; set; }
}
